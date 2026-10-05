namespace Nitrogenesis.Sim.Map;

/// <summary>Single-threaded Dijkstra over any <see cref="IReverseGraph"/>, with a binary heap.</summary>
public static class Dijkstra
{
    /// <summary>
    /// Shortest distances to the goal set. <paramref name="dist"/> must hold 0 for each source (goal) node
    /// and +∞ for every other node; on return each entry is its cost to the nearest source (+∞ if none).
    /// </summary>
    /// <remarks>
    /// Deterministic: the heap order depends only on the inputs, so equal inputs give bit-identical output.
    /// Uses lazy deletion (a node may be pushed several times; stale entries are skipped).
    /// </remarks>
    public static void Run<TGraph>(TGraph graph, double[] dist) where TGraph : struct, IReverseGraph
    {
        var heap = new MinHeap(dist.Length);
        for (int i = 0; i < dist.Length; i++)
            if (dist[i] == 0) heap.Push(0, i);

        Span<int> from = graph.MaxIncoming <= 64 ? stackalloc int[64] : new int[graph.MaxIncoming];
        Span<double> cost = graph.MaxIncoming <= 64 ? stackalloc double[64] : new double[graph.MaxIncoming];

        while (heap.Count > 0)
        {
            (double d, int node) = heap.Pop();
            if (d > dist[node]) continue; // stale entry
            int n = graph.GetIncoming(node, from, cost);
            for (int k = 0; k < n; k++)
            {
                double nd = d + cost[k];
                int v = from[k];
                if (nd < dist[v])
                {
                    dist[v] = nd;
                    heap.Push(nd, v);
                }
            }
        }
    }

    /// <summary>Array-backed binary min-heap of (key, node). Ties pop in a fixed, input-determined order.</summary>
    private sealed class MinHeap(int capacity)
    {
        private double[] _keys = new double[Math.Max(capacity, 16)];
        private int[] _nodes = new int[Math.Max(capacity, 16)];

        public int Count { get; private set; }

        public void Push(double key, int node)
        {
            if (Count == _keys.Length)
            {
                Array.Resize(ref _keys, Count * 2);
                Array.Resize(ref _nodes, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (_keys[parent] <= key) break;
                _keys[i] = _keys[parent];
                _nodes[i] = _nodes[parent];
                i = parent;
            }
            _keys[i] = key;
            _nodes[i] = node;
        }

        public (double Key, int Node) Pop()
        {
            var top = (_keys[0], _nodes[0]);
            int last = --Count;
            double key = _keys[last];
            int node = _nodes[last];
            int i = 0;
            while (true)
            {
                int child = 2 * i + 1;
                if (child >= last) break;
                if (child + 1 < last && _keys[child + 1] < _keys[child]) child++;
                if (_keys[child] >= key) break;
                _keys[i] = _keys[child];
                _nodes[i] = _nodes[child];
                i = child;
            }
            _keys[i] = key;
            _nodes[i] = node;
            return top;
        }
    }
}
