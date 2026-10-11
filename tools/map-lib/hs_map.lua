#!ra3luabridge
-- HSTest v3: money variants in ONE click (cap / give / set / team-form / index-form)
exCenterTopBtnsChangeVisibility(1)
exCenterTopBtnShow(3, 'Button_PlayerPower_ProductionKickback', 'MONEY')
exCenterTopBtnShow(4, 'Button_PlayerPower_ProductionKickback', 'POWER')

function onCenterTopBtnClickEvent(player_name, button_id)
    local p = tostring(player_name)
    if button_id == 3 then
        local r = {}
        r[1] = tostring(exPlayerGetCurrentMoney(p))
        ExecuteAction("PLAYER_SET_MONEY_CAP", p, 999999)
        ExecuteAction("PLAYER_GIVE_MONEY", p, 300001)
        r[2] = tostring(exPlayerGetCurrentMoney(p))
        ExecuteAction("PLAYER_SET_MONEY", p, 250000)
        r[3] = tostring(exPlayerGetCurrentMoney(p))
        ExecuteAction("PLAYER_GIVE_MONEY", p .. "/team" .. p, 1000)
        r[4] = tostring(exPlayerGetCurrentMoney(p))
        ExecuteAction("PLAYER_GIVE_MONEY", 1, 1000)
        r[5] = tostring(exPlayerGetCurrentMoney(p))
        exMessageAppendToMessageArea('HS start=' .. r[1] .. ' cap+give=' .. r[2] .. ' set=' .. r[3] .. ' team=' .. r[4] .. ' idx=' .. r[5])
    elseif button_id == 4 then
        ExecuteAction("PLAYER_SET_BASE_POWER", p, 777)
        exMessageAppendToMessageArea('HS power=' .. tostring(exPlayerGetProducePower(p)))
    end
end
