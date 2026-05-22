namespace Soundpad.Audio;

public class SoundCache
{
    private readonly ISoundDecoder _decoder;
    private readonly int _capacity;
    private readonly LinkedList<(string Key, CachedSound Value)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, CachedSound Value)>> _index = new();
    private readonly object _lock = new();

    public SoundCache(ISoundDecoder decoder, int capacity = 50)
    {
        _decoder = decoder;
        _capacity = capacity;
    }

    public CachedSound Get(string filePath)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(filePath, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Value;
            }
            var decoded = _decoder.Decode(filePath);
            var newNode = new LinkedListNode<(string, CachedSound)>((filePath, decoded));
            _order.AddFirst(newNode);
            _index[filePath] = newNode;
            if (_index.Count > _capacity)
            {
                var lru = _order.Last!;
                _order.RemoveLast();
                _index.Remove(lru.Value.Key);
            }
            return decoded;
        }
    }
}
