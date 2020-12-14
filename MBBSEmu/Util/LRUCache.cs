using System.Collections.Concurrent;
using System.Collections.Generic;
using System;

namespace MBBSEmu.Util
{
  /// <summary>
  ///   An Least Recently Used cache. Stores data up to MaxSize elements, and any attempts to add
  ///   new items will purge the least recently used/accessed item.
  /// </summary>
  /// <typeparam name="TKey"></typeparam>
  /// <typeparam name="TValue"></typeparam>
  public class LRUCache<TKey, TValue> : IDictionary<TKey,TValue>
  {
    /// <summary>
    ///   Data stored inside the Dictionary.
    ///
    ///   <para/>Keeps track of the raw data as well as a reference to the LinkedListNode for
    ///   efficient removal/insertion (O(1) vs O(n)).
    /// </summary>
    private class Data
    {
      public Data(TKey key, TValue data)
      {
        _data = data;
        _recentlyUsedNode = new LinkedListNode<TKey>(key);
      }

      public TValue _data;
      public readonly LinkedListNode<TKey> _recentlyUsedNode;
    }

    /// <summary>
    ///   Holds all the data.
    /// </summary>
    private readonly ConcurrentDictionary<TKey, Data> _data = new();
    /// <summary>
    ///   The list used for keeping track of the most recently used items.
    ///
    ///   <para/>Front of the list is the most recently used, with the rear being the least.
    /// </summary>
    /// <returns></returns>
    private readonly LinkedList<TKey> _recentlyUsedList = new();

    /// <summary>
    ///   The maximum number of items this collection will hold.
    /// </summary>
    /// <value></value>
    public int MaxSize { get; init; }

    public LRUCache(int maxSize)
    {
      if (maxSize <= 0)
        throw new ArgumentException("LRUCache needs to have size > 0");

      MaxSize = maxSize;
    }

    private Data InsertNewItem(TKey key, TValue value)
    {
      if (Count >= MaxSize)
      {
        // purge an item
        _data.Remove(_recentlyUsedList.Last.Value, out _);
        _recentlyUsedList.RemoveLast();
      }

      return new Data(key, value);
    }

    public TValue this[TKey key]
    {
      get
      {
        var data = _data[key];
        _recentlyUsedList.Remove(data._recentlyUsedNode);
        _recentlyUsedList.AddFirst(data._recentlyUsedNode);
        return data._data;
      }
      set
      {
        var newValue = _data.AddOrUpdate(key, key => InsertNewItem(key, value), (key, oldValue) => {
          if (oldValue != null)
            _recentlyUsedList.Remove(oldValue._recentlyUsedNode);

          oldValue._data = value;
          return oldValue;
        });

        _recentlyUsedList.AddFirst(newValue._recentlyUsedNode);
      }
    }

    public int Count { get => _data.Count; }

    public int ListCount { get => _recentlyUsedList.Count; }
    public TKey MostRecentlyUsed { get => _recentlyUsedList.First.Value; }

    public bool IsReadOnly { get => false; }

    public System.Collections.Generic.ICollection<TKey> Keys { get => _data.Keys; }

    public System.Collections.Generic.ICollection<TValue> Values { get => throw new NotSupportedException(); }

    public void Add(KeyValuePair<TKey,TValue> item) => this[item.Key] = item.Value;

    public void Clear() => _data.Clear();

    public bool Contains(KeyValuePair<TKey, TValue> item) => _data.ContainsKey(item.Key);

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => throw new NotSupportedException();

    public bool Remove(KeyValuePair<TKey, TValue> item) => throw new NotSupportedException();

    public void Add(TKey key, TValue value) => this[key] = value;

    public bool ContainsKey(TKey key) => _data.ContainsKey(key);

    public bool Remove(TKey key) => throw new NotSupportedException();

    public bool TryGetValue(TKey key, out TValue value)
    {
      var ret = _data.TryGetValue(key, out var v);
      value = v._data;
      return ret;
    }

    public IEnumerator<KeyValuePair<TKey,TValue>> GetEnumerator() => throw new NotSupportedException();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => throw new NotSupportedException();
  }
}
