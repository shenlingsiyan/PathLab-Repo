namespace RMG_V1_1
{
    internal static partial class Program
    {
        // ============================================================
        // 坐标级地形投影
        //
        // 地形与通行属性的唯一来源仍是 MapData 的 Obstacles / Spawn / Exit：
        // 不另建一套格子网格，避免与生成状态重复、失配。
        // 自然格代价沿用 P0 寻路内核的既有规则（每步 1）。
        // 不可跨越用 TraversalType 显式表达，而非巨大代价。
        // ============================================================

        private enum TerrainType
        {
            Empty,
            NaturalObstacle,
            Spawn,
            Exit
        }

        private enum TraversalType
        {
            Traversable,
            Impassable
        }

        private readonly struct TerrainCell
        {
            public readonly TerrainType TerrainType;
            public readonly TraversalType TraversalType;
            public readonly int? Cost;   // 仅 Traversable 格有值

            public TerrainCell(TerrainType terrainType, TraversalType traversalType, int? cost)
            {
                TerrainType = terrainType;
                TraversalType = traversalType;
                Cost = cost;
            }
        }

        private static class TerrainCatalog
        {
            // P0 契约：自然格 = 1 步。
            public const int TraversableCost = 1;

            public static TerrainCell Describe(TerrainType terrain)
                => terrain == TerrainType.NaturalObstacle
                    ? new TerrainCell(terrain, TraversalType.Impassable, null)
                    : new TerrainCell(terrain, TraversalType.Traversable, TraversableCost);
        }
    }
}
