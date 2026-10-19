using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 把同一張三維格子，依目前深度軸讀成「這個畫面看得到的方塊」。
/// </summary>
public static class FezGridProjection
{
    public struct Axes
    {
        public Vector3 depth;
        public Vector3 lateral;
    }

    private static readonly Dictionary<long, FezGridBlock> Shallowest = new Dictionary<long, FezGridBlock>(64);

    /// <summary>下落穿過期間：這塊一般方塊頂面暫時不可踩，並可對它執行繞過。</summary>
    private static FezGridBlock IgnoredFloorBlock;

    public static void SetIgnoredFloor(FezGridBlock block) => IgnoredFloorBlock = block;

    public static void ClearIgnoredFloor() => IgnoredFloorBlock = null;

    public static bool IsIgnoredFloor(FezGridBlock block) =>
        block != null && block == IgnoredFloorBlock;

    public static Axes FromYaw(float yawDegrees)
    {
        var snapped = Mathf.Round(yawDegrees / 90f) * 90f;
        var quadrant = Mathf.RoundToInt(Mathf.Repeat(snapped, 360f) / 90f) % 4;
        Vector3 depth;
        switch (quadrant)
        {
            case 0: depth = Vector3.forward; break;
            case 1: depth = Vector3.right; break;
            case 2: depth = Vector3.back; break;
            default: depth = Vector3.left; break;
        }

        return new Axes
        {
            depth = depth,
            lateral = Vector3.Cross(Vector3.up, depth)
        };
    }

    public static float DepthOf(Vector3 worldPos, Axes axes) => Vector3.Dot(worldPos, axes.depth);

    public static float LateralOf(Vector3 worldPos, Axes axes) => Vector3.Dot(worldPos, axes.lateral);

    /// <summary>
    /// 同一畫面柱、同一高度只留下離鏡頭最近的一格。其餘視為被擋住。
    /// </summary>
    public static void CollectVisible(FezGridMap map, Axes axes, List<FezGridBlock> results)
    {
        results.Clear();
        if (map == null)
            return;

        IndexShallowest(map, axes);
        foreach (var pair in Shallowest)
            results.Add(pair.Value);
    }

    /// <summary>
    /// 平常移動站在這一格最淺的地板。
    /// holdDepth 只在轉完視角、人還被較淺的方塊擋住時使用：先留在現在的深度，走出遮擋再回到最淺。
    /// </summary>
    public static bool TryResolveFloor(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float maxDrop,
        float bodyHeight,
        bool holdDepth,
        out FezGridBlock floor,
        out bool covered)
    {
        floor = null;
        covered = false;
        if (map == null)
            return false;

        var playerDepth = DepthOf(feet, axes);
        covered = holdDepth && IsCovered(map, feet, axes, bodyHeight);
        IndexShallowest(map, axes);

        var bestGap = float.PositiveInfinity;
        var bestDepthScore = float.PositiveInfinity;
        var plantedFloor = false;

        foreach (var block in map.Blocks)
        {
            // 平常只能踩這一格畫面最前面看得到的實體；被更淺的地形或遮片擋住的都不能踩。
            if (!covered && !IsShallowestOnFace(block, axes))
                continue;

            if (!IsFloorCandidate(map, block, feet, axes, maxDrop, out var gap))
                continue;

            // 腳還踩在這格上時，不要被同一畫面柱裡更淺的地板拉走。轉視角才不會往淺處彈。
            var planted = IsFeetPlantedOn(block, feet);
            if (planted)
            {
                if (!plantedFloor || gap < bestGap)
                {
                    plantedFloor = true;
                    bestGap = gap;
                    floor = block;
                }

                continue;
            }

            if (plantedFloor)
                continue;

            if (covered)
            {
                var depth = DepthOf(block.Center, axes);
                if (depth < playerDepth - 0.75f)
                    continue;

                var score = Mathf.Abs(depth - playerDepth);
                if (score < bestDepthScore - 0.001f
                    || (Mathf.Abs(score - bestDepthScore) <= 0.001f && gap < bestGap))
                {
                    bestDepthScore = score;
                    bestGap = gap;
                    floor = block;
                }
            }
            else if (floor == null
                || IsShallower(block, floor, axes)
                || (!IsShallower(floor, block, axes) && gap < bestGap))
            {
                bestGap = gap;
                floor = block;
            }
        }

        return floor != null;
    }

    /// <summary>
    /// 腳下這一格看得到、且頂面在腳附近的地板。深度差很多也算同一條路。
    /// </summary>
    public static bool TryGetFloor(
        List<FezGridBlock> visible,
        Vector3 feet,
        Axes axes,
        float maxDrop,
        out FezGridBlock floor)
    {
        floor = null;
        var bestGap = float.PositiveInfinity;

        for (var i = 0; i < visible.Count; i++)
        {
            var block = visible[i];
            if (block == null)
                continue;

            var lateralDistance = Mathf.Abs(LateralOf(feet, axes) - LateralOf(block.Center, axes));
            if (lateralDistance > 0.42f)
                continue;

            var gap = feet.y - block.TopY;
            if (gap < -0.08f || gap > maxDrop)
                continue;

            if (gap < bestGap)
            {
                bestGap = gap;
                floor = block;
            }
        }

        return floor != null;
    }

    /// <summary>
    /// 玩家身體四角（畫面側向 × 高度）各一個偵測點。
    /// 任一角前方有更淺的方塊蓋住該點 → 視為被遮擋；四角都沒被蓋住 → 離開遮擋。
    /// </summary>
    public static bool IsCovered(FezGridMap map, Vector3 feet, Axes axes, float bodyHeight)
    {
        if (map == null)
            return false;

        var playerDepth = DepthOf(feet, axes);
        FillBodyCornerProbes(feet, axes, bodyHeight, CornerLat, CornerY);

        for (var i = 0; i < 4; i++)
        {
            if (IsProbeOccluded(map, axes, playerDepth, CornerLat[i], CornerY[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 取得四角偵測點的世界座標，以及該點目前是否被遮擋（給 Scene Gizmo 用）。
    /// 順序：左下、右下、左上、右上。
    /// </summary>
    public static void GetCoverProbeWorldPoints(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float bodyHeight,
        Vector3[] worldPoints,
        bool[] occluded)
    {
        if (worldPoints == null || worldPoints.Length < 4)
            return;

        var playerDepth = DepthOf(feet, axes);
        FillBodyCornerProbes(feet, axes, bodyHeight, CornerLat, CornerY);

        for (var i = 0; i < 4; i++)
        {
            worldPoints[i] = CornerProbeToWorld(axes, CornerLat[i], CornerY[i], playerDepth);
            if (occluded != null && occluded.Length > i)
                occluded[i] = map != null && IsProbeOccluded(map, axes, playerDepth, CornerLat[i], CornerY[i]);
        }
    }

    private static readonly float[] CornerLat = new float[4];
    private static readonly float[] CornerY = new float[4];

    private static void FillBodyCornerProbes(Vector3 feet, Axes axes, float bodyHeight, float[] lat, float[] y)
    {
        var centerLat = LateralOf(feet, axes);
        const float halfW = 0.28f;
        var y0 = feet.y + 0.25f;
        var y1 = feet.y + Mathf.Max(0.45f, bodyHeight * 0.9f);

        // 左下、右下、左上、右上（畫面側向 × 高度）
        lat[0] = centerLat - halfW;
        y[0] = y0;
        lat[1] = centerLat + halfW;
        y[1] = y0;
        lat[2] = centerLat - halfW;
        y[2] = y1;
        lat[3] = centerLat + halfW;
        y[3] = y1;
    }

    private static Vector3 CornerProbeToWorld(Axes axes, float probeLat, float probeY, float depth)
    {
        return axes.lateral * probeLat + Vector3.up * probeY + axes.depth * depth;
    }

    private static bool IsProbeOccluded(
        FezGridMap map,
        Axes axes,
        float playerDepth,
        float probeLat,
        float probeY)
    {
        foreach (var block in map.Blocks)
        {
            if (block == null)
                continue;

            // 必須明顯在玩家前方（更淺）。
            if (DepthOf(block.Center, axes) > playerDepth - 0.5f)
                continue;

            var bLat = LateralOf(block.Center, axes);
            if (probeLat < bLat - 0.5f || probeLat > bLat + 0.5f)
                continue;

            var bY0 = block.Cell.y;
            var bY1 = block.Cell.y + 1f;
            if (probeY < bY0 || probeY > bY1)
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// 四角已離開遮擋、人卻還在更淺方塊後面時：撥到那些方塊再淺一格。
    /// 地形／遮片都算；只看畫面側向是否還對到，不要求高度重疊（否則跳離／走出剪影後繞不到）。
    /// </summary>
    public static bool TryResolveLeaveCoverRoute(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float bodyHeight,
        out float targetDepth)
    {
        targetDepth = 0f;
        if (map == null)
            return false;

        var playerDepth = DepthOf(feet, axes);
        var feetLat = LateralOf(feet, axes);
        var bestFront = float.PositiveInfinity;
        var foundFront = false;

        foreach (var block in map.Blocks)
        {
            // 邊界牆不參與「離開遮擋後往淺側撥」；遮片牆仍可當前方遮擋並繞行。
            if (block == null || block.IsBoundaryWall)
                continue;

            var blockDepth = DepthOf(block.Center, axes);
            if (blockDepth > playerDepth - 0.5f)
                continue;

            if (Mathf.Abs(LateralOf(block.Center, axes) - feetLat) > 0.55f)
                continue;

            if (blockDepth < bestFront)
            {
                bestFront = blockDepth;
                foundFront = true;
            }
        }

        if (!foundFront)
            return false;

        return TryFindClearRouteDepth(map, feet, axes, bodyHeight, bestFront - 1f, out targetDepth);
    }

    private static bool IsFloorCandidate(FezGridMap map, FezGridBlock block, Vector3 feet, Axes axes, float maxDrop, out float gap)
    {
        gap = 0f;
        if (block == null || !block.Solid)
            return false;

        if (IsIgnoredFloor(block))
            return false;

        if (map != null && map.HasSolid(block.Cell + Vector3Int.up))
            return false;

        var lateralDistance = Mathf.Abs(LateralOf(feet, axes) - LateralOf(block.Center, axes));
        if (lateralDistance > 0.55f)
            return false;

        gap = feet.y - block.TopY;
        return gap >= -0.08f && gap <= maxDrop;
    }

    /// <summary>
    /// 腳的水平位置還在這格裡，而且高度就在頂面附近。
    /// </summary>
    public static bool IsFeetPlantedOn(FezGridBlock block, Vector3 feet)
    {
        if (block == null || !block.Solid || IsIgnoredFloor(block))
            return false;

        var cell = block.Cell;
        if (feet.x < cell.x - 0.02f || feet.x > cell.x + 1.02f)
            return false;
        if (feet.z < cell.z - 0.02f || feet.z > cell.z + 1.02f)
            return false;

        var gap = feet.y - block.TopY;
        return gap >= -0.15f && gap <= 0.35f;
    }

    public static Vector3 SnapDepth(Vector3 feet, FezGridBlock floor, Axes axes)
    {
        return SnapToDepth(feet, DepthOf(floor.Center, axes), axes);
    }

    public static Vector3 SnapToDepth(Vector3 feet, float targetDepth, Axes axes)
    {
        var current = DepthOf(feet, axes);
        return feet + axes.depth * (targetDepth - current);
    }

    /// <summary>
    /// 在指定深度上，膠囊身體是否會嵌進實體方塊（站在頂面不算）。
    /// </summary>
    public static bool BodyBlockedAtDepth(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float depth,
        float bodyHeight,
        float radius = 0.32f)
    {
        if (map == null)
            return false;

        RefreshShallowest(map, axes);

        var feetLateral = LateralOf(feet, axes);
        var bodyMin = feet.y + 0.05f;
        var bodyMax = feet.y + Mathf.Max(0.2f, bodyHeight);

        foreach (var block in map.Blocks)
        {
            if (block == null)
                continue;

            var isPlatform = block.Solid;
            var isBoundary = block.IsBoundaryWall;
            if (!isPlatform && !isBoundary)
                continue;

            // 下落穿過中的那塊不擋身體，才能穿過去並繞行。
            if (isPlatform && IsIgnoredFloor(block))
                continue;

            var blockDepth = DepthOf(block.Center, axes);
            if (Mathf.Abs(blockDepth - depth) > 0.55f)
                continue;

            if (Mathf.Abs(feetLateral - LateralOf(block.Center, axes)) > radius)
                continue;

            var blockMin = block.Cell.y;
            var blockMax = block.Cell.y + 1f;
            // 平台：腳踩在頂面上方不算卡住。邊界牆頂面不可踩，重疊即擋。
            if (isPlatform && feet.y >= blockMax - 0.02f)
                continue;

            if (blockMin < bodyMax && blockMax > bodyMin)
                return true;
        }

        return false;
    }

    /// <summary>
    /// S+空白：對腳下那塊一般方塊執行繞過（頂面已暫時不可踩）。
    /// </summary>
    public static bool TryResolveDropThroughRoute(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float bodyHeight,
        FezGridBlock platform,
        out float targetDepth)
    {
        targetDepth = 0f;
        if (map == null || platform == null || !platform.Solid)
            return false;

        var preferred = DepthOf(platform.Center, axes) - 1f;
        return TryFindClearRouteDepth(map, feet, axes, bodyHeight, preferred, out targetDepth);
    }

    /// <summary>
    /// 若已嵌進實體，沿深度往更淺方向推出，直到身體不再重疊。
    /// 不可越過邊界牆到其更淺側。
    /// </summary>
    public static Vector3 DepenetrateShallow(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float bodyHeight,
        float maxPush = 4f)
    {
        if (map == null)
            return feet;

        var depth = DepthOf(feet, axes);
        if (!BodyBlockedAtDepth(map, feet, axes, depth, bodyHeight))
            return feet;

        const float step = 0.25f;
        for (var pushed = step; pushed <= maxPush + 0.001f; pushed += step)
        {
            var probeDepth = depth - pushed;
            var probe = SnapToDepth(feet, probeDepth, axes);

            // 再往淺會撞上邊界牆 → 停住，避免被推到牆的淺側。
            if (BoundaryWallBlocksAtDepth(map, probe, axes, probeDepth, bodyHeight))
                break;

            if (!BodyBlockedAtDepth(map, probe, axes, probeDepth, bodyHeight))
                return probe;
        }

        return feet;
    }

    /// <summary>
    /// 側面繞行：遮片，以及「一般方塊的側面」。
    /// 腳踩在一般方塊頂面時不當繞行（維持可踩踏）；軀幹對到側面時撥到該塊再淺一格。
    /// keepRoute 只在還對到同一柱時續航。
    /// </summary>
    public static bool TryResolveWallBypass(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float bodyHeight,
        bool keepRoute,
        out float targetDepth)
    {
        targetDepth = 0f;
        if (map == null)
            return false;

        var playerDepth = DepthOf(feet, axes);
        var found = false;
        var bestRoute = float.PositiveInfinity;
        // 進入時稍微往前探；續航時要比進入更嚴，否則會在牆邊來回黏住。
        var lateralLimit = keepRoute ? 0.48f : 0.62f;

        foreach (var block in map.Blocks)
        {
            if (block == null)
                continue;

            // 邊界牆改由 Shell 硬擋；遮片牆維持深度繞行。
            if (block.IsBoundaryWall)
                continue;

            // 一般方塊：踩在頂面＝地板，不繞；只繞側面。
            if (block.Solid && IsStandingOnBlockTop(block, feet, axes))
                continue;

            // 遮片一律可繞；一般方塊僅在軀幹對到側面時繞。
            if (!OverlapsWallColumn(block, feet, axes, bodyHeight, lateralLimit))
                continue;

            var wallDepth = DepthOf(block.Center, axes);
            var preferred = wallDepth - 1f;

            // 已到繞行深度（牆／方塊的淺側）：只有續航中、人還對著這根柱時才留下。
            if (playerDepth <= preferred + 0.08f)
            {
                if (!keepRoute)
                    continue;
                if (playerDepth < preferred - 0.4f)
                    continue;

                if (preferred < bestRoute)
                {
                    bestRoute = preferred;
                    found = true;
                }

                continue;
            }

            // 人比目標深、同深、或還在往淺側移動途中：找一格不會嵌進實體的繞行深度。
            if (!TryFindClearRouteDepth(map, feet, axes, bodyHeight, preferred, out var route))
                continue;

            if (route < bestRoute)
            {
                bestRoute = route;
                found = true;
            }
        }

        if (!found)
            return false;

        targetDepth = bestRoute;
        return true;
    }

    /// <summary>
    /// 腳在這塊一般方塊頂面附近 → 當地板踩，不當側面繞行。
    /// </summary>
    private static bool IsStandingOnBlockTop(FezGridBlock block, Vector3 feet, Axes axes)
    {
        if (block == null || !block.Solid)
            return false;

        // 下落穿過中：不當「踩在頂面」，允許對這塊走側面繞行。
        if (IsIgnoredFloor(block))
            return false;

        var lateral = Mathf.Abs(LateralOf(feet, axes) - LateralOf(block.Center, axes));
        if (lateral > 0.55f)
            return false;

        var gap = feet.y - block.TopY;
        return gap >= -0.08f && gap <= 0.35f;
    }

    private static bool TryFindClearRouteDepth(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float bodyHeight,
        float preferredRoute,
        out float route)
    {
        route = preferredRoute;
        for (var i = 0; i < 4; i++)
        {
            var candidate = preferredRoute - i;
            var probe = SnapToDepth(feet, candidate, axes);

            // 此深度被邊界牆擋住 → 不可再往更淺繞過去。
            if (BoundaryWallBlocksAtDepth(map, probe, axes, candidate, bodyHeight))
                return false;

            if (!BodyBlockedAtDepth(map, probe, axes, candidate, bodyHeight))
            {
                route = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 指定深度上身體是否與邊界牆重疊（硬擋，深度繞行不可穿越）。
    /// </summary>
    private static bool BoundaryWallBlocksAtDepth(
        FezGridMap map,
        Vector3 feet,
        Axes axes,
        float depth,
        float bodyHeight,
        float radius = 0.32f)
    {
        if (map == null)
            return false;

        var feetLateral = LateralOf(feet, axes);
        var bodyMin = feet.y + 0.05f;
        var bodyMax = feet.y + Mathf.Max(0.2f, bodyHeight);

        foreach (var block in map.Blocks)
        {
            if (block == null || !block.IsBoundaryWall)
                continue;

            var blockDepth = DepthOf(block.Center, axes);
            if (Mathf.Abs(blockDepth - depth) > 0.55f)
                continue;

            if (Mathf.Abs(feetLateral - LateralOf(block.Center, axes)) > radius)
                continue;

            var blockMin = block.Cell.y;
            var blockMax = block.Cell.y + 1f;
            if (blockMin < bodyMax && blockMax > bodyMin)
                return true;
        }

        return false;
    }

    private static bool OverlapsWallColumn(
        FezGridBlock block,
        Vector3 feet,
        Axes axes,
        float bodyHeight,
        float lateralLimit)
    {
        var lateralDistance = Mathf.Abs(LateralOf(feet, axes) - LateralOf(block.Center, axes));
        if (lateralDistance > lateralLimit)
            return false;

        // 只用軀幹中段對牆，避免腳尖／頭頂擦到隔壁牆柱就一直續航。
        var bodyMin = feet.y + 0.2f;
        var bodyMax = feet.y + Mathf.Max(0.35f, bodyHeight * 0.85f);
        var blockMin = block.Cell.y;
        var blockMax = block.Cell.y + 1f;
        return blockMin < bodyMax && blockMax > bodyMin;
    }

    /// <summary>
    /// 重建各側向柱上最淺方塊索引（平台／遮片／邊界牆都算）。
    /// </summary>
    public static void RefreshShallowest(FezGridMap map, Axes axes)
    {
        IndexShallowest(map, axes);
    }

    private static void IndexShallowest(FezGridMap map, Axes axes)
    {
        Shallowest.Clear();
        if (map == null)
            return;

        // 地形、遮片、邊界牆都算「擋在前面」。遮片／邊界牆本身不能踩，但會讓後面的實體地板算被遮住。
        foreach (var block in map.Blocks)
        {
            if (block == null)
                continue;

            var key = ColumnKey(block.Cell, axes);
            if (!Shallowest.TryGetValue(key, out var current) || IsShallower(block, current, axes))
                Shallowest[key] = block;
        }
    }

    /// <summary>
    /// 此格是否為該側向柱上最淺（當前視角看得見的最前格）。呼叫前需 RefreshShallowest／相關查詢已建索引。
    /// </summary>
    public static bool IsShallowestOnFace(FezGridBlock block, Axes axes)
    {
        if (block == null)
            return false;

        if (!Shallowest.TryGetValue(ColumnKey(block.Cell, axes), out var front) || front == null)
            return true;

        return DepthOf(block.Center, axes) <= DepthOf(front.Center, axes) + 0.001f;
    }

    private static long ColumnKey(Vector3Int cell, Axes axes)
    {
        var column = Mathf.Abs(axes.lateral.x) > 0.5f ? cell.x : cell.z;
        return ((long)column << 32) ^ (uint)cell.y;
    }

    private static bool IsShallower(FezGridBlock candidate, FezGridBlock current, Axes axes)
    {
        return DepthOf(candidate.Center, axes) < DepthOf(current.Center, axes);
    }
}
