using System;
using System.Collections.Generic;

/// <summary>Pure graph search; independent of physics and presentation.</summary>
public static class EnemyAStar
{
    /// <summary>Choose the closest point only among nodes connected to the start.</summary>
    public static bool SearchClosest(int count, int start, Func<int, IList<int>> neighbours,
        Func<int, int, float> cost, Func<int, float> distanceToTarget, List<int> result)
    {
        result.Clear();
        if (start < 0 || start >= count) return false;
        var visited = new bool[count];
        var queue = new Queue<int>();
        queue.Enqueue(start); visited[start] = true;
        int best = start;
        float bestDistance = distanceToTarget(start);
        while (queue.Count > 0)
        {
            int node = queue.Dequeue();
            float distance = distanceToTarget(node);
            if (distance < bestDistance) { best = node; bestDistance = distance; }
            foreach (int next in neighbours(node))
            {
                if (visited[next]) continue;
                visited[next] = true; queue.Enqueue(next);
            }
        }
        return Search(count, start, best, neighbours, cost, i => 0f, result);
    }
    private struct Entry { public int node; public float priority; }
    public static bool Search(int count, int start, int goal, Func<int, IList<int>> neighbours,
        Func<int, int, float> cost, Func<int, float> heuristic, List<int> result)
    {
        result.Clear();
        if (start < 0 || goal < 0 || start >= count || goal >= count) return false;
        var scores = new float[count];
        var parents = new int[count];
        var closed = new bool[count];
        for (int i = 0; i < count; i++) { scores[i] = float.PositiveInfinity; parents[i] = -1; }
        var heap = new List<Entry>();
        scores[start] = 0f;
        Push(heap, new Entry { node = start, priority = heuristic(start) });
        while (heap.Count > 0)
        {
            int current = Pop(heap).node;
            if (closed[current]) continue;
            if (current == goal)
            {
                for (int node = goal; node >= 0; node = parents[node]) result.Add(node);
                result.Reverse();
                return true;
            }
            closed[current] = true;
            foreach (int next in neighbours(current))
            {
                float candidate = scores[current] + cost(current, next);
                if (candidate >= scores[next]) continue;
                scores[next] = candidate;
                parents[next] = current;
                closed[next] = false;
                Push(heap, new Entry { node = next, priority = candidate + heuristic(next) });
            }
        }
        return false;
    }

    private static void Push(List<Entry> heap, Entry entry)
    {
        heap.Add(entry);
        int index = heap.Count - 1;
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (heap[parent].priority <= entry.priority) break;
            heap[index] = heap[parent];
            index = parent;
        }
        heap[index] = entry;
    }
    private static Entry Pop(List<Entry> heap)
    {
        var result = heap[0];
        var last = heap[heap.Count - 1];
        heap.RemoveAt(heap.Count - 1);
        if (heap.Count == 0) return result;
        int index = 0;
        while (index * 2 + 1 < heap.Count)
        {
            int child = index * 2 + 1;
            if (child + 1 < heap.Count && heap[child + 1].priority < heap[child].priority) child++;
            if (heap[child].priority >= last.priority) break;
            heap[index] = heap[child];
            index = child;
        }
        heap[index] = last;
        return result;
    }
}
