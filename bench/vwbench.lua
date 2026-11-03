-- The frame-rate run of docs/benchmark.md, for Garry's Mod (client realm, sv_cheats 1).
-- VWBench.Run(pathFile, label, speed, done): flies the view along the path in data/<pathFile> at a
-- constant speed, looking ahead along it, and prints the average FPS and the 1% low; done, if given,
-- gets that line. The first two seconds are not counted, while the view settles.

VWBench = VWBench or {}

function VWBench.Run(pathFile, label, speed, done)
    speed = speed or 400
    local points = util.JSONToTable(file.Read(pathFile, "DATA") or "")
    if not points or #points < 2 then print("[vwbench] no path in data/" .. pathFile) return end
    local path, total = {}, 0
    for i, p in ipairs(points) do
        path[i] = { pos = Vector(p[1], p[2], p[3]), at = total }
        if points[i + 1] then total = total + Vector(p[1], p[2], p[3]):Distance(Vector(points[i + 1][1], points[i + 1][2], points[i + 1][3])) end
    end
    local function At(d)
        for i = 1, #path - 1 do
            local a, b = path[i], path[i + 1]
            if d <= b.at then
                local t = (d - a.at) / math.max(b.at - a.at, 1)
                -- look a few steps ahead, so the view turns smoothly along the path
                local ahead = path[math.min(i + 3, #path)].pos
                return LerpVector(t, a.pos, b.pos), (ahead - a.pos):Angle()
            end
        end
        return path[#path].pos, (path[#path].pos - path[#path - 1].pos):Angle()
    end

    if LocalPlayer():GetMoveType() ~= MOVETYPE_NOCLIP then RunConsoleCommand("noclip") end
    local start, times, warm = RealTime(), {}, 2
    local view = { origin = path[1].pos, angles = Angle() }
    -- the view is set exactly each frame; the player follows, so the server's PVS goes with it
    hook.Add("CalcView", "vwbench", function()
        return { origin = view.origin, angles = view.angles, fov = 90, drawviewer = false }
    end)
    hook.Add("Think", "vwbench", function()
        local elapsed = RealTime() - start
        local d = math.max(0, elapsed - warm) * speed
        local pos, ang = At(d)
        ang.p = math.Clamp(ang.p, -30, 30)
        ang.r = 0
        view.origin, view.angles = pos, ang
        RunConsoleCommand("setpos", pos.x, pos.y, pos.z - 64)
        if elapsed > warm then times[#times + 1] = FrameTime() end
        if d >= total then
            hook.Remove("Think", "vwbench")
            hook.Remove("CalcView", "vwbench")
            local sum = 0
            for _, t in ipairs(times) do sum = sum + t end
            table.sort(times)
            local low = times[math.max(1, math.floor(#times * 0.99))]
            local line = string.format("[vwbench] %s frames %d seconds %.2f avg %.1f low1 %.1f", label, #times, sum, #times / sum, 1 / low)
            print(line)
            if done then done(line) end
        end
    end)
end
