using System.Collections.Generic;

namespace UnityDebugAdapter
{
  public class Handles<T>
  {
    private const int START_HANDLE = 1000;

    private int _nextHandle;
    private readonly Dictionary<int, T> _handleMap;

    public Handles()
    {
      _nextHandle = START_HANDLE;
      _handleMap = new Dictionary<int, T>();
    }

    public void Reset()
    {
      _nextHandle = START_HANDLE;
      _handleMap.Clear();
    }

    public int Create(T value)
    {
      var handle = _nextHandle++;
      _handleMap[handle] = value;
      return handle;
    }

    public bool TryGet(int handle, out T value)
    {
      if (_handleMap.TryGetValue(handle, out value))
      {
        return true;
      }
      return false;
    }

    public T Get(int handle, T dflt)
    {
      if (_handleMap.TryGetValue(handle, out T value))
      {
        return value;
      }
      return dflt;
    }
  }
}
