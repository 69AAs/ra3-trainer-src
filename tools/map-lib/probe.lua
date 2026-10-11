--[[ HS 探针 v1：一次性查清地图 Lua 上下文里有什么
     目的：① 验证 .map 注入格式是否真能让脚本生效（两个按钮 = 生效）
           ② 逐个测试 API 是否存在，把结果直接写进消息区
           ③ 不依赖任何外部文件、不用 getn/#、不用 string.gmatch（避免 Lua 版本差异）
]]--

local probe = {}
local function note(s)
  probe[table.getn(probe) + 1] = tostring(s)
end

note('boot')
if exMessageAppendToMessageArea ~= nil then note('msgApi=yes') else note('msgApi=NO') end
if exCenterTopBtnsChangeVisibility ~= nil then note('visApi=yes') else note('visApi=NO') end
if exCenterTopBtnShow ~= nil then note('btnApi=yes') else note('btnApi=NO') end
if ExecuteAction ~= nil then note('execApi=yes') else note('execApi=NO') end
if loadfile ~= nil then note('loadfile=yes') else note('loadfile=NO') end
if pcall ~= nil then note('pcall=yes') else note('pcall=NO') end
if table.getn ~= nil then note('getn=yes') else note('getn=NO') end
if string.gmatch ~= nil then note('gmatch=yes') else note('gmatch=NO') end
if SchedulerModule ~= nil then note('sched=yes') else note('sched=NO') end

-- 按钮：只要注入生效，顶部一定会出现这两个
if exCenterTopBtnsChangeVisibility ~= nil then
  exCenterTopBtnsChangeVisibility(1)
end
if exCenterTopBtnShow ~= nil then
  exCenterTopBtnShow(2, 'Button_PlayerPower_ProductionKickback', 'HS-TEST')
  exCenterTopBtnShow(3, 'Button_PlayerPower_ProductionKickback', 'HS2')
end

-- 消息区：把探针结果打出来（单人局消息区可能不显示，所以按钮才是主信号）
if exMessageAppendToMessageArea ~= nil then
  exMessageAppendToMessageArea('[HS] ' .. table.concat(probe, ' '))
end

function onCenterTopBtnClickEvent(player_name, button_id)
  if exMessageAppendToMessageArea ~= nil then
    exMessageAppendToMessageArea('[HS] clicked ' .. tostring(button_id) .. ' by ' .. tostring(player_name))
  end
  if ExecuteAction ~= nil then
    ExecuteAction("PLAYER_SET_MONEY", tostring(player_name), 384001)
  end
end
