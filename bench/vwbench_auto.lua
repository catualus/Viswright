-- Runs the whole frame-rate plan unattended (docs/benchmark.md). Goes in lua/autorun/ with vwbench.lua
-- in lua/; does nothing unless data/vwbench/active.txt exists. The plan is data/vwbench/plan.json, a
-- list of { map, path, speed }; each map is flown three times, the lines are appended to
-- data/vwbench/results.txt, and the next map is loaded.

if not file.Exists("vwbench/active.txt", "DATA") then return end

if SERVER then
    AddCSLuaFile()
    AddCSLuaFile("vwbench.lua")
    hook.Add("PlayerSpawn", "vwbench_auto", function(ply) timer.Simple(0, function() if IsValid(ply) then ply:GodEnable() end end) end)
    -- the client asks for the next map by writing its name
    timer.Create("vwbench_auto", 1, 0, function()
        local nxt = file.Read("vwbench/next.txt", "DATA")
        if nxt and nxt ~= "" then
            file.Delete("vwbench/next.txt")
            game.ConsoleCommand("map " .. nxt .. "\n")
        end
    end)
    return
end

include("vwbench.lua")

local RUNS = 3
local SETTINGS = {
    mat_monitorgamma = "2.2", mat_autoexposure_max = "1", mat_autoexposure_min = "1", mat_bloomscale = "0",
    mat_vsync = "0", fps_max = "0", cl_drawhud = "0", r_drawviewmodel = "0", engine_no_focus_sleep = "0",
}

hook.Add("InitPostEntity", "vwbench_auto", function()
    local plan = util.JSONToTable(file.Read("vwbench/plan.json", "DATA") or "") or {}
    local index = tonumber(file.Read("vwbench/index.txt", "DATA") or "1") or 1
    local item = plan[index]
    local function Finish()
        print("[vwbench] plan finished")
        file.Delete("vwbench/active.txt")
    end
    if not item then return Finish() end
    if game.GetMap() ~= item.map then
        file.Write("vwbench/next.txt", item.map)
        return
    end
    -- Lua may not set some of these; those are set once from the console before the plan starts
    for name, value in pairs(SETTINGS) do pcall(RunConsoleCommand, name, value) end

    -- let the map settle (textures, auto-exposure) before the first run
    timer.Simple(8, function()
        local run = 0
        local function One()
            run = run + 1
            VWBench.Run(item.path, item.map .. " run" .. run, item.speed, function(line)
                file.Append("vwbench/results.txt", line .. "\n")
                if run < RUNS then return timer.Simple(2, One) end
                file.Write("vwbench/index.txt", tostring(index + 1))
                if plan[index + 1] then file.Write("vwbench/next.txt", plan[index + 1].map) else Finish() end
            end)
        end
        One()
    end)
end)
