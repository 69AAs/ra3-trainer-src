--[[
  R3 地图助手 v2（日冕 / RA3 地图 lua 库）
  ------------------------------------------------------------------
  挂载方式：地图文件夹里，和 <图名>.map 并排放一个同名文件夹：
      Maps/<地图文件夹>/<图名>.map
      Maps/<地图文件夹>/<图名>/init.lua        <- 本文件
      Maps/<地图文件夹>/<图名>/.lib_meta.json
  用 tools/map-lib/install-map-lib.ps1 一键装/卸（只新建这一个文件夹，不动 .map）。
  **脚本必须先被"导入进地图"**（地编插件 RA3MapUtil_LuaImporter 干的）；装好之后改本文件立刻生效。

  用法（进图后）—— 三条入口，任选：
    ① 顶栏按钮（鼠标，不用键盘）：
         2 = 发兵      3 = 钱 100000      4 = 电力 100000      5 = 秘技点 15
    ② 聊天框打字（**只有联机局有聊天框**）：
         5 发兵 | 8/2 换兵种 | 4/6 改数量 | 3 换阵营 | 7 换落点 | 9 看配置 | 0 帮助
         money / power / sp      = 直接设成默认值
         money 50000             = 设成指定值
    ③ 配置轮询（外部写文件驱动，不需要进游戏操作）：
         把 hs-config.lua 写到地图目录（或启动器自定义搜索路径）：
             return { command = "money 50000", command_id = 12345,
                      money = 100000, power = 100000, sp = 15 }
         command_id 变了就执行一次；改 money/power/sp 则点按钮时用新值。

  为什么走地图 lua 而不是内存写入：
    出的兵/改的值都是**游戏自己按官方动作名做的**（走 ExecuteAction），所以不会出现
    "游戏未解析单位类型 / 可能已部分生成" 这类失败；也不需要读写游戏内存。
  反馈全部走游戏自己的消息区，不用 OSD、不用键盘钩子，零输入冲突。
]]--

R3 = {
  faction = 1,
  index = 1,
  count = 1,
  waypoint = 1,
  -- 落点候选：<玩家>_Start 是每张遭遇战图**一定存在**的航点（例子图用的是 Waypoint 3，
  -- 但 "Waypoint 1" 在多数图里并不存在 —— 写死它等于发兵必然失败）。
  waypoints = { 'SELF_START', 'Waypoint 1', 'Waypoint 2', 'Waypoint 3' },
  spawned = 0,
  button = 2,                       -- 顶部按钮槽位（1=系统占用，从 2 起）
  button_icon = 'Button_PlayerPower_ProductionKickback',
  -- 资源默认值（会被 hs-config.lua 覆盖）
  money = 100000,
  power = 100000,
  sp = 15,
  cmd_id = 0,
  factions = { '盟军', '苏联', '帝国' },
  units = {
  { 'AlliedBomberAircraft', '盟军 · 世纪轰炸机' },
  { 'AlliedGunshipAircraft', '盟军 · 先锋武装炮艇机' },
  { 'AlliedSupportAircraft', '盟军 · 冰冻直升机' },
  { 'AlliedMCV', '盟军 · 基地车' },
  { 'AlliedAntiAirVehicleTech1', '盟军 · 多功能步兵战车' },
  { 'AlliedAntiVehicleVehicleTech1', '盟军 · 守护者坦克' },
  { 'AlliedEngineer', '盟军 · 工兵' },
  { 'AlliedArtilleryVehicle', '盟军 · 平定者（ArtilleryVehicle）' },
  { 'AlliedAntiVehicleVehicleTech3', '盟军 · 幻影坦克' },
  { 'AlliedFutureTank', '盟军 · 未来坦克X-1' },
  { 'AlliedAntiVehicleInfantry', '盟军 · 标枪兵' },
  { 'AlliedAntiAirShip', '盟军 · 水翼船' },
  { 'AlliedAntiNavalScout', '盟军 · 海豚' },
  { 'AlliedAntiInfantryVehicle_Ground', '盟军 · 激流ACV（Ground）' },
  { 'SovietAntiStructureVehicle', '苏联 · V4导弹发射车' },
  { 'SovietAntiGroundAircraft', '苏联 · 双刃直升机' },
  { 'SovietSurveyor', '苏联 · 史普尼克勘查车' },
  { 'SovietMCV', '苏联 · 基地车' },
  { 'SovietBomberAircraft', '苏联 · 基洛夫飞艇' },
  { 'SovietAntiVehicleVehicleTech3', '苏联 · 天启坦克' },
  { 'SovietCommandoTech1', '苏联 · 娜塔莎' },
  { 'SovietAntiInfantryInfantry', '苏联 · 征召兵' },
  { 'SovietScoutVehicle', '苏联 · 恐怖机器人' },
  { 'SovietEngineer', '苏联 · 战斗工兵' },
  { 'SovietScoutInfantry', '苏联 · 战熊' },
  { 'SovietAntiStructureShip', '苏联 · 无畏级武库舰' },
  { 'SovietMortarCycle', '苏联 · 火炮机车' },
  { 'SovietAntiAirShip_Ground', '苏联 · 牛蛙战车（Ground）' },
  { 'JapanPowerPlantEgg', '帝国 · 发电机核心' },
  { 'JapanAntiVehicleInfantry', '帝国 · 坦克杀手' },
  { 'JapanMCV', '帝国 · 基地车' },
  { 'JapanBaseDefenseAdvancedEgg', '帝国 · 塔楼核心' },
  { 'JapanAntiInfantryVehicle', '帝国 · 天狗机器人' },
  { 'JapanAntiStructureShip', '帝国 · 将军战列舰（AntiStructureShip）' },
  { 'JapanEngineer', '帝国 · 工兵' },
  { 'JapanAntiInfantryInfantry', '帝国 · 帝国武士' },
  { 'JapanArcherInfantry', '帝国 · 弓箭少女' },
  { 'JapanInfiltrationInfantry', '帝国 · 忍者' },
  { 'JapanAntiAirVehicleTech1', '帝国 · 打击者-VX' },
  { 'JapanWarFactoryEgg', '帝国 · 机甲工厂核心' },
  { 'JapanCommandoTech1', '帝国 · 欧米伽百合子' },
  { 'JapanSuperWeaponAdvancedEgg', '帝国 · 毁灭装置核心' },
  }
}

-- 当前阵营能用的兵种（units 表按阵营分组，这里筛出来）
function R3.pool()
  local name = R3.factions[R3.faction]
  local out = {}
  for i = 1, getn(R3.units) do
    local entry = R3.units[i]
    if string.find(entry[2], name, 1, true) == 1 then
      out[getn(out) + 1] = entry
    end
  end
  if getn(out) == 0 then out = R3.units end
  return out
end

function R3.say(text)
  if exMessageAppendToMessageArea then exMessageAppendToMessageArea('[R3] ' .. tostring(text)) end
end

function R3.current()
  local pool = R3.pool()
  if R3.index > getn(pool) then R3.index = 1 end
  return pool[R3.index], pool
end

function R3.spawn(player_name)
  local unit = R3.current()
  if unit == nil then R3.say('兵种表是空的'); return end
  local spot = R3.waypoints[R3.waypoint]
  if spot == 'SELF_START' then spot = tostring(player_name) .. '_Start' end
  for i = 1, R3.count do
    R3.spawned = R3.spawned + 1
    ExecuteAction("CREATE_NAMED_ON_TEAM_AT_WAYPOINT",
      "r3_unit_" .. tostring(R3.spawned),
      unit[1],
      tostring(player_name) .. "/team" .. tostring(player_name),
      spot)
  end
  R3.say('发兵 ' .. tostring(R3.count) .. ' × ' .. unit[2] .. '  落点 ' .. spot)
end

-- ---- 新增：资源（走官方动作名，参数类型见 ScriptActionNew.json）----
function R3.set_money(player_name, amount)
  local value = tonumber(amount) or R3.money
  ExecuteAction("PLAYER_SET_MONEY", tostring(player_name), value)
  R3.say('钱 → ' .. tostring(value))
end

function R3.set_power(player_name, amount)
  local value = tonumber(amount) or R3.power
  ExecuteAction("PLAYER_SET_BASE_POWER", tostring(player_name), value)
  R3.say('电力 → ' .. tostring(value))
end

function R3.set_sp(player_name, amount)
  local value = tonumber(amount) or R3.sp
  ExecuteAction("PLAYER_SET_PLAYER_POWER_POINTS", tostring(player_name), value)
  R3.say('秘技点 → ' .. tostring(value))
end

function R3.report()
  local unit = R3.current()
  R3.say('阵营 ' .. R3.factions[R3.faction] .. ' | 兵种 ' .. (unit and unit[2] or '?') ..
         ' | 数量 ' .. tostring(R3.count) .. ' | 落点 ' .. R3.waypoints[R3.waypoint] ..
         ' | 已发 ' .. tostring(R3.spawned) ..
         ' | 钱 ' .. tostring(R3.money) .. ' 电力 ' .. tostring(R3.power) .. ' 秘技点 ' .. tostring(R3.sp))
end

function R3.help()
  R3.say('按钮: 2 发兵 | 3 钱 | 4 电力 | 5 秘技点')
  R3.say('聊天: 5 发兵 | 8/2 换兵种 | 4/6 改数量 | 3 换阵营 | 7 换落点 | 9 看配置 | 0 帮助')
  R3.say('聊天: money [值] | power [值] | sp [值]')
end

-- ---- 配置读取（外部写文件驱动）：找不到就算了，绝不影响其它功能 ----
function R3.load_config()
  if loadfile == nil then return false end
  local names = { 'hs-config.lua', 'R3Training/hs-config.lua', 'R3Lua/hs-config.lua' }
  for i = 1, getn(names) do
    local chunk = nil
    pcall(function() chunk = loadfile(names[i]) end)
    if chunk ~= nil then
      local ok, cfg = pcall(chunk)
      if ok and type(cfg) == 'table' then
        if cfg.money then R3.money = tonumber(cfg.money) or R3.money end
        if cfg.power then R3.power = tonumber(cfg.power) or R3.power end
        if cfg.sp then R3.sp = tonumber(cfg.sp) or R3.sp end
        if cfg.unit_type then R3.forced_unit = tostring(cfg.unit_type) end
        if cfg.command and cfg.command_id then
          local id = tonumber(cfg.command_id) or 0
          if id ~= 0 and id ~= R3.cmd_id then
            R3.cmd_id = id
            R3.run_command(tostring(cfg.command), tostring(cfg.command_player or 'Player_1'))
          end
        end
        return true
      end
    end
  end
  return false
end

-- 执行外部下发的一条命令（"money 50000" / "unit" / "cfg" …）
function R3.run_command(text, player_name)
  local words = {}
  for w in string.gmatch(tostring(text), "%S+") do words[getn(words) + 1] = w end
  if getn(words) == 0 then return end
  local cmd = string.lower(words[1])
  local arg = words[2]
  if cmd == 'money' then R3.set_money(player_name, arg)
  elseif cmd == 'power' then R3.set_power(player_name, arg)
  elseif cmd == 'sp' then R3.set_sp(player_name, arg)
  elseif cmd == 'unit' or cmd == 'tank' then R3.spawn(player_name)
  elseif cmd == 'help' then R3.help()
  elseif cmd == 'cfg' then R3.report()
  end
end

-- 聊天指令（原生日志：onUserShortChatMsg(player_name, content)）
function onUserShortChatMsg(player_name, content)
  local raw = tostring(content)
  local cmd = string.gsub(raw, "%s", "")
  if cmd == '5' then
    R3.spawn(player_name)
  elseif cmd == '8' then
    R3.index = R3.index - 1; if R3.index < 1 then R3.index = getn(R3.pool()) end
    R3.report()
  elseif cmd == '2' then
    R3.index = R3.index + 1; if R3.index > getn(R3.pool()) then R3.index = 1 end
    R3.report()
  elseif cmd == '4' then
    R3.count = max(1, R3.count - 1); R3.report()
  elseif cmd == '6' then
    R3.count = min(50, R3.count + 1); R3.report()
  elseif cmd == '3' then
    R3.faction = R3.faction + 1; if R3.faction > getn(R3.factions) then R3.faction = 1 end
    R3.index = 1; R3.report()
  elseif cmd == '7' then
    R3.waypoint = R3.waypoint + 1; if R3.waypoint > getn(R3.waypoints) then R3.waypoint = 1 end
    R3.report()
  elseif cmd == '9' then
    R3.report()
  elseif cmd == '0' or cmd == 'help' then
    R3.help()
  else
    -- 文本命令（money / power / sp / unit / cfg …）
    local first = string.lower(cmd)
    if first == 'money' or first == 'power' or first == 'sp' or first == 'unit'
       or first == 'tank' or first == 'cfg' then
      R3.run_command(raw, player_name)
    end
  end
end

-- ---- 顶部按钮（鼠标路径，不需要键盘）----
-- 2 = 发兵（原有）｜3 = 钱｜4 = 电力｜5 = 秘技点
if exCenterTopBtnsChangeVisibility then
  exCenterTopBtnsChangeVisibility(1)
  exCenterTopBtnShow(R3.button, R3.button_icon, '发兵')
  exCenterTopBtnShow(3, R3.button_icon, '钱')
  exCenterTopBtnShow(4, R3.button_icon, '电力')
  exCenterTopBtnShow(5, R3.button_icon, '秘技点')
end

function onCenterTopBtnClickEvent(player_name, button_id)
  if button_id == R3.button then
    R3.spawn(player_name)
  elseif button_id == 3 then
    R3.set_money(player_name, nil)
  elseif button_id == 4 then
    R3.set_power(player_name, nil)
  elseif button_id == 5 then
    R3.set_sp(player_name, nil)
  elseif button_id == 6 then
    ExecuteAction("PLAYER_SET_MONEY", tostring(player_name), 384001)
    R3.say('自证: money 384001')
  end
end

-- ---- 配置轮询（每 X 帧；有就用，没有就算了）----
R3.load_config()
if SchedulerModule and SchedulerModule.call_every_x_frame then
  pcall(function()
    SchedulerModule.call_every_x_frame(function() R3.load_config() end, 30)
  end)
end

R3.say('已装载 v2：按钮 2发兵 3钱 4电力 5秘技点；按 0 看帮助')

-- [auto] 自证块：进图那一刻就做两件外部可核对的事
pcall(function() ExecuteAction("PLAYER_SET_MONEY", "Player_1", 384001) end)
pcall(function() exMessageAppendToMessageArea('[R3] v3 loaded -> money 384001') end)
if exCenterTopBtnShow then pcall(function() exCenterTopBtnShow(6, R3.button_icon, 'R3V3') end) end
