using System;
using System.Collections.Generic;

/// <summary>Search regression can run without Unity physics or a loaded scene.</summary>
public static class EnemyAStarRegression
{
#if UNITY_EDITOR
    [UnityEditor.MenuItem("Morld/Tests/Run A Star Search Regression")]
    private static void MenuRun() { Run(); UnityEngine.Debug.Log("[Morld] A* search regression passed (107 cases)."); }
#endif
    public static void Run()
    {
        var result = new List<int>();
        IList<int>[] weighted = { new List<int> { 1, 2 }, new List<int> { 3 }, new List<int> { 1, 3 }, new List<int>() };
        Func<int, int, float> cost = (a, b) => a == 0 && b == 1 ? 8f : a == 2 && b == 3 ? 9f : 1f;
        Require(EnemyAStar.Search(4, 0, 3, i => weighted[i], cost, i => 0f, result), "weighted path missing");
        Require(string.Join(",", result) == "0,2,1,3", "weighted path was not shortest");
        Require(EnemyAStar.Search(4, 2, 2, i => weighted[i], cost, i => 0f, result) && result.Count == 1, "same-node path");
        Require(!EnemyAStar.Search(4, 3, 0, i => weighted[i], cost, i => 0f, result) && result.Count == 0, "disconnected graph");
        Require(!EnemyAStar.Search(0, -1, -1, i => weighted[i], cost, i => 0f, result), "empty graph");
        IList<int>[] disconnected = { new List<int> { 1 }, new List<int> { 0, 2 }, new List<int> { 1 }, new List<int>() };
        float[] targetDistances = { 10f, 5f, 2f, 0f };
        Require(EnemyAStar.SearchClosest(4, 0, i => disconnected[i], (a, b) => 1f, i => targetDistances[i], result)
            && string.Join(",", result) == "0,1,2", "closest fallback selected unreachable target component");
        Require(EnemyAStar.SearchClosest(4, 0, i => disconnected[i], (a, b) => 1f, i => i == 0 ? 0f : 10f, result)
            && result.Count == 1 && result[0] == 0, "already closest must stay put");
        Require(EnemyAStar.SearchClosest(4, 3, i => disconnected[i], (a, b) => 1f, i => i, result)
            && result.Count == 1 && result[0] == 3, "isolated agent crossed a gap");
        var random = new Random(2341);
        for (int trial = 0; trial < 100; trial++)
        {
            const int width = 8, count = width * width;
            var blocked = new bool[count];
            for (int i = 1; i < count - 1; i++) blocked[i] = random.NextDouble() < 0.3;
            var edges = new List<int>[count];
            for (int i = 0; i < count; i++)
            {
                edges[i] = new List<int>();
                if (blocked[i]) continue;
                int x = i % width, z = i / width;
                foreach (int candidate in new[] { x > 0 ? i - 1 : -1, x < width - 1 ? i + 1 : -1, z > 0 ? i - width : -1, z < width - 1 ? i + width : -1 })
                    if (candidate >= 0 && !blocked[candidate]) edges[i].Add(candidate);
            }
            var distances = new int[count];
            for (int i = 0; i < count; i++) distances[i] = -1;
            var queue = new Queue<int>(); queue.Enqueue(0); distances[0] = 0;
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in edges[current])
                {
                    if (distances[next] >= 0) continue;
                    distances[next] = distances[current] + 1;
                    queue.Enqueue(next);
                }
            }
            bool found = EnemyAStar.Search(count, 0, count - 1, i => edges[i], (a, b) => 1f,
                i => width - 1 - i % width + width - 1 - i / width, result);
            Require(found == (distances[count - 1] >= 0), "grid reachability mismatch");
            if (!found) continue;
            Require(result.Count - 1 == distances[count - 1], "grid shortest-distance mismatch");
            for (int i = 1; i < result.Count; i++) Require(edges[result[i - 1]].Contains(result[i]), "path crosses wall");
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("A* regression: " + message); }
}
