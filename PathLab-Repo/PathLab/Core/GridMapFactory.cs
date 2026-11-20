using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PathLab.Core;

/// <summary>
/// Two map sources: the RMG V1.1 generator (when its executable is built)
/// and a small self-contained fallback with the same shape (boundary gates,
/// clustered walls, a few cost patches).
/// </summary>
public static class GridMapFactory
{
    private const int RmgSize = 16;

    public static GridMap CreateRandom(int width, int height, int seed)
    {
        var random = new Random(seed);
        var map = new GridMap(width, height);

        map.Start = PickBoundary(random, width, height, null);
        map.Goal = PickBoundary(random, width, height, map.Start);

        // Clustered walls, roughly 22% of the map.
        int budget = (int)(width * height * 0.22);
        int placed = 0;
        int guard = 0;

        while (placed < budget && guard++ < budget * 20)
        {
            var center = new Cell(random.Next(width), random.Next(height));
            if (center == map.Start || center == map.Goal || map.IsWall(center)) continue;

            int targetSize = random.Next(3, 8);
            var cluster = new HashSet<Cell> { center };
            var frontier = new List<Cell> { center };

            while (cluster.Count < targetSize && frontier.Count > 0)
            {
                int index = random.Next(frontier.Count);
                Cell current = frontier[index];
                frontier.RemoveAt(index);

                foreach (Cell next in map.Neighbours(current).OrderBy(_ => random.Next()))
                {
                    if (next == map.Start || next == map.Goal) continue;
                    if (map.IsWall(next) || !cluster.Add(next)) continue;
                    frontier.Add(next);
                    if (cluster.Count >= targetSize) break;
                }
            }

            foreach (Cell cell in cluster)
            {
                if (placed >= budget) break;
                if (cell == map.Start || cell == map.Goal || map.IsWall(cell)) continue;
                map.SetCost(cell, GridMap.Wall);
                placed++;
            }
        }

        // Guarantee that the gates stay connected: carve one monotone staircase.
        CarveCorridor(map, random);

        // A few weighted patches so weighted and heuristic search differ visibly.
        PaintTerrainPatches(map, random);

        map.SetCost(map.Start, GridMap.MinCost);
        map.SetCost(map.Goal, GridMap.MinCost);
        return map;
    }

    /// <summary>
    /// Runs `RMG_V1_1.exe --size 16 --seed S --cells TMP` and converts the
    /// per-cell CSV into a GridMap. Any failure is reported, never thrown.
    /// </summary>
    public static bool TryCreateFromRmg(int seed, out GridMap map, out string note)
    {
        map = new GridMap(RmgSize, RmgSize);
        note = string.Empty;

        string? executable = LocateRmgExecutable();
        if (executable is null)
        {
            note = "未找到 RMG_V1_1.exe";
            return false;
        }

        string csvPath = Path.Combine(Path.GetTempPath(), $"pathlab_rmg_{Guid.NewGuid():N}.csv");

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--size");
            startInfo.ArgumentList.Add(RmgSize.ToString());
            startInfo.ArgumentList.Add("--seed");
            startInfo.ArgumentList.Add(seed.ToString());
            startInfo.ArgumentList.Add("--cells");
            startInfo.ArgumentList.Add(csvPath);

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                note = "无法启动 RMG";
                return false;
            }

            // Redirected stdin makes the generator's WaitForExitKey() return immediately.
            try { process.StandardInput.Close(); } catch (Exception) { }

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                note = "RMG 超时";
                return false;
            }

            // Drain the redirected streams so the process can fully exit.
            try { Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 1000); } catch (Exception) { }

            if (process.ExitCode != 0 || !File.Exists(csvPath))
            {
                note = $"RMG 未产出地图（exit {process.ExitCode}）";
                return false;
            }

            if (!TryParseCellsCsv(csvPath, RmgSize, RmgSize, out map, out string reason))
            {
                note = reason;
                return false;
            }

            note = "RMG v3";
            return true;
        }
        catch (Exception ex)
        {
            note = ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(csvPath)) File.Delete(csvPath); } catch (Exception) { }
        }
    }

    private static bool TryParseCellsCsv(string path, int width, int height, out GridMap map, out string reason)
    {
        map = new GridMap(width, height);
        reason = string.Empty;

        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            reason = "cells.csv 为空";
            return false;
        }

        bool hasSpawn = false;
        bool hasExit = false;
        int seen = 0;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;

            string[] parts = line.Split(',');
            if (parts.Length < 5)
            {
                reason = $"cells.csv 第 {i + 1} 行字段不足";
                return false;
            }

            if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y))
            {
                reason = $"cells.csv 第 {i + 1} 行坐标非法";
                return false;
            }

            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                reason = $"cells.csv 坐标越界 ({x},{y})";
                return false;
            }

            var cell = new Cell(x, y);
            string terrain = parts[2].Trim();
            string traversal = parts[3].Trim();
            string costText = parts[4].Trim();

            if (traversal.Equals("Impassable", StringComparison.OrdinalIgnoreCase)
                || costText.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                map.SetCost(cell, GridMap.Wall);
            }
            else if (int.TryParse(costText, out int cost))
            {
                map.SetCost(cell, cost);
            }
            else
            {
                reason = $"cells.csv 第 {i + 1} 行代价非法：'{costText}'";
                return false;
            }

            if (terrain.Equals("Spawn", StringComparison.OrdinalIgnoreCase))
            {
                map.Start = cell;
                hasSpawn = true;
            }
            else if (terrain.Equals("Exit", StringComparison.OrdinalIgnoreCase))
            {
                map.Goal = cell;
                hasExit = true;
            }

            seen++;
        }

        if (seen != width * height)
        {
            reason = $"cells.csv 有 {seen} 个坐标，期望 {width * height}";
            return false;
        }

        if (!hasSpawn || !hasExit)
        {
            reason = "cells.csv 缺少 Spawn 或 Exit";
            return false;
        }

        map.SetCost(map.Start, GridMap.MinCost);
        map.SetCost(map.Goal, GridMap.MinCost);
        return true;
    }

    private static Cell PickBoundary(Random random, int width, int height, Cell? excluded)
    {
        var pool = new List<Cell>();

        for (int x = 0; x < width; x++)
        {
            pool.Add(new Cell(x, 0));
            pool.Add(new Cell(x, height - 1));
        }

        for (int y = 1; y < height - 1; y++)
        {
            pool.Add(new Cell(0, y));
            pool.Add(new Cell(width - 1, y));
        }

        if (excluded.HasValue)
        {
            Cell anchor = excluded.Value;
            int farthest = pool.Max(c => Manhattan(c, anchor));
            int threshold = (int)Math.Ceiling(farthest * 0.75);
            List<Cell> preferred = pool.Where(c => Manhattan(c, anchor) >= threshold).ToList();
            if (preferred.Count > 0) pool = preferred;
        }

        return pool[random.Next(pool.Count)];
    }

    private static void CarveCorridor(GridMap map, Random random)
    {
        int x = map.Start.X;
        int y = map.Start.Y;
        int guard = 0;
        int limit = map.Width * map.Height * 4;

        while ((x != map.Goal.X || y != map.Goal.Y) && guard++ < limit)
        {
            int dx = Math.Sign(map.Goal.X - x);
            int dy = Math.Sign(map.Goal.Y - y);

            bool moveX;
            if (dx != 0 && dy != 0) moveX = random.NextDouble() < 0.5;
            else moveX = dx != 0;

            if (moveX) x += dx;
            else if (dy != 0) y += dy;
            else break;

            var cell = new Cell(x, y);
            if (cell != map.Start && cell != map.Goal)
                map.SetCost(cell, GridMap.MinCost);
        }
    }

    private static void PaintTerrainPatches(GridMap map, Random random)
    {
        int patchCount = random.Next(2, 5);

        for (int p = 0; p < patchCount; p++)
        {
            int guard = 0;
            Cell seed;
            do
            {
                seed = new Cell(random.Next(map.Width), random.Next(map.Height));
            }
            while ((map.IsWall(seed) || seed == map.Start || seed == map.Goal) && guard++ < 200);

            if (map.IsWall(seed) || seed == map.Start || seed == map.Goal) continue;

            int value = random.Next(2, 5);
            int targetSize = random.Next(5, 13);
            var patch = new HashSet<Cell> { seed };
            var frontier = new List<Cell> { seed };

            while (patch.Count < targetSize && frontier.Count > 0)
            {
                int index = random.Next(frontier.Count);
                Cell current = frontier[index];
                frontier.RemoveAt(index);

                foreach (Cell next in map.Neighbours(current).OrderBy(_ => random.Next()))
                {
                    if (next == map.Start || next == map.Goal || map.IsWall(next)) continue;
                    if (!patch.Add(next)) continue;
                    frontier.Add(next);
                    if (patch.Count >= targetSize) break;
                }
            }

            foreach (Cell cell in patch)
            {
                if (cell == map.Start || cell == map.Goal) continue;
                map.SetCost(cell, Math.Max(map.CostAt(cell), value));
            }
        }
    }

    private static string? LocateRmgExecutable()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (int depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
        {
            foreach (string configuration in new[] { "Release", "Debug" })
            {
                string candidate = Path.Combine(
                    directory.FullName, "RMG V1_1 SYSTEM", "bin", configuration, "net10.0", "RMG_V1_1.exe");

                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static int Manhattan(Cell a, Cell b)
        => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
