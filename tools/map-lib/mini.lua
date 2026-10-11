-- 最小验证：不做任何计算/函数定义，只调官方 API；把 Lua 版本和 API 得分写在按钮标签上
local n = 0
if getn ~= nil then n = n + 1 end
if table ~= nil and table.getn ~= nil then n = n + 10 end
if string.gmatch ~= nil then n = n + 100 end
if pcall ~= nil then n = n + 1000 end
local ver = tostring(_VERSION)
if exCenterTopBtnsChangeVisibility ~= nil then exCenterTopBtnsChangeVisibility(1) end
if exCenterTopBtnShow ~= nil then
  exCenterTopBtnShow(2, 'Button_PlayerPower_ProductionKickback', 'A')          -- 只要出现 A => 我们的脚本跑了
  exCenterTopBtnShow(3, 'Button_PlayerPower_ProductionKickback', ver)         -- Lua 版本
  exCenterTopBtnShow(4, 'Button_PlayerPower_ProductionKickback', 'n' .. tostring(n))  -- API 得分
end
if exMessageAppendToMessageArea ~= nil then exMessageAppendToMessageArea('[HS] ' .. ver .. ' score=' .. tostring(n)) end
