"""The camera path for a map's frame-rate run (docs/benchmark.md): waypoints picked from its standing
points, spread across the map and visited in a short tour, walked from one to the next over the
standing points themselves, so the camera never passes through a wall, and every variant is flown the
same way.

Usage: python -I path.py <points.json> <path.json> [waypoints=12]
"""
import json, math, sys
from collections import deque

points = [tuple(p) for p in json.load(open(sys.argv[1]))]
count = int(sys.argv[3]) if len(sys.argv) > 3 else 12
GRID = 128

# Standing points are on a 128-unit grid; two are joined when they are grid neighbours, with a step
# up or down a player could walk (stairs, ramps).
cells = {}
for i, (x, y, z) in enumerate(points):
    cells.setdefault((math.floor(x / GRID), math.floor(y / GRID)), []).append(i)

def neighbours(i):
    x, y, z = points[i]
    cx, cy = math.floor(x / GRID), math.floor(y / GRID)
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            if dx == dy == 0:
                continue
            for j in cells.get((cx + dx, cy + dy), ()):
                if abs(points[j][2] - z) <= 128:
                    yield j

def bfs(start):
    prev = {start: None}
    queue = deque([start])
    while queue:
        i = queue.popleft()
        for j in neighbours(i):
            if j not in prev:
                prev[j] = i
                queue.append(j)
    return prev

# the largest walkable region
seen, largest = set(), []
for i in range(len(points)):
    if i not in seen:
        region = list(bfs(i))
        seen.update(region)
        if len(region) > len(largest):
            largest = region
inside = set(largest)

# spread: each next waypoint is the point of the region farthest from those already picked
picked = [min(largest, key=lambda i: points[i])]
far = {k: math.dist(points[k], points[picked[0]]) for k in largest}
while len(picked) < min(count, len(largest)):
    i = max(largest, key=lambda k: far[k])
    picked.append(i)
    for k in largest:
        far[k] = min(far[k], math.dist(points[k], points[i]))

# a short tour through them: nearest unvisited next
tour = [picked[0]]
left = set(picked[1:])
while left:
    here = points[tour[-1]]
    nxt = min(left, key=lambda k: math.dist(points[k], here))
    tour.append(nxt)
    left.remove(nxt)

# walk each leg over the standing points
walk = [tour[0]]
for a, b in zip(tour, tour[1:]):
    prev = bfs(a)
    leg, i = [], b
    while i != a:
        leg.append(i)
        i = prev[i]
    walk.extend(reversed(leg))

json.dump([points[i] for i in walk], open(sys.argv[2], "w"))
length = sum(math.dist(points[a], points[b]) for a, b in zip(walk, walk[1:]))
print(f"path: {len(tour)} waypoints, {len(walk)} steps, {length:.0f} units, region of {len(largest)} of {len(points)} points")
