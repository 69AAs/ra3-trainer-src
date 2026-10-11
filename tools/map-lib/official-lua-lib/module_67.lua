LoggerModule.setup(LoggerLevelEnum.INFO, {LoggerTargetEnum.CHAT_MSG})



Example05_meta = {

    things = {ThingEnum.AlliedCommandoTech1, ThingEnum.AlliedFighterAircraft, ThingEnum.CelestialAntiAirShip},

    button_index = 2,

    Player_1 = 1,

    Player_2 = 1

}



TopButtonModule.set_visible(1, nil)

TopButtonModule.add_or_update_button(Example05_meta.button_index, 'Button_PlayerPower_ProductionKickback', '获得随机单位', nil)





TopButtonModule.register_button_click_callback(function(player_name, button_id)

    if button_id == Example05_meta.button_index and Example05_meta[player_name] == 99 then

        ChatMsgModule.print_on_chat(player_name .. " clicked button")



        local choosed_thing = RandomHelper.choice(Example05_meta.things)

        UnitCreateHelper.create_unit_at_waypoint(

            UnitHelper.get_unit_auto_name(),

            choosed_thing,

            TeamModule.from_player_name(player_name),

            "Waypoint 3",

            nil, nil, nil

        )

        Example05_meta[player_name] = 1

    end

end)



SchedulerModule.call_every_x_frame(

    function() 

        for i = 1, 2 do

            local player_name = "Player_" .. i

            Example05_meta[player_name] = min(99, Example05_meta[player_name] + 1)



            TopButtonModule.set_button_time_overlay_value(Example05_meta.button_index, Example05_meta[player_name], player_name)

            TopButtonModule.set_button_hp_var_value(Example05_meta.button_index, Example05_meta[player_name], player_name)



            local ready = Example05_meta[player_name] == 99



            TopButtonModule.set_button_time_overlay_visible(Example05_meta.button_index, not ready, player_name)

            TopButtonModule.set_button_hp_bar_visible(Example05_meta.button_index, not ready, player_name)



            TopButtonModule.set_button_highlight(Example05_meta.button_index, ready, player_name)

            -- TopButtonModule.set_button_selected(Example05_meta.button_index, ready, player_name)

        end

    end,

 1)
