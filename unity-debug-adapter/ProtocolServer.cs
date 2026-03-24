using System;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace UnityDebugAdapter
{
  /// <summary>
  /// Can be used to implement a debug adapter protocol
  /// </summary>
  public abstract class ProtocolServer
  {
    protected static readonly int BUFFER_SIZE = 4096;
    protected static Regex CONTENT_LENGTH_MATCHER = new Regex(@"Content-Length: (\d+)\r\n\r\n");

    private int _sequenceNumber = 1;
    // TODO: use concurrent Dictionary instead...
    private readonly Dictionary<int, TaskCompletionSource<Response>> _pendingRequests
      = new Dictionary<int, TaskCompletionSource<Response>>();

    private Stream _outputStream;

    private readonly ByteBuffer _rawData = new ByteBuffer();
    private int _bodyLength = -1;

    private bool _stopRequested;

    public async Task Start(Stream inputStream, Stream outputStream)
    {
      _outputStream = outputStream;

      byte[] buffer = new byte[BUFFER_SIZE];

      _stopRequested = false;
      while (!_stopRequested)
      {
        var read = await inputStream.ReadAsync(buffer, 0, buffer.Length);

        if (read == 0)
        {
          // end of stream
          Logger.LogTrace("end of stream reached - exiting the debug adapter");
          break;
        }

        if (read > 0)
        {
          _rawData.Append(buffer, read);
          ProcessData();
        }
      }
    }


    public void Stop()
    {
      _stopRequested = true;
    }


    public void SendEvent(Event e)
    {
      SendMessage(e);
    }


    public Task<Response> SendRequest(string command, object args)
    {
      var tcs = new TaskCompletionSource<Response>();

      Request request = null;
      lock (_pendingRequests)
      {
        request = new Request(_sequenceNumber++, command, args);

        // wait for response
        _pendingRequests.Add(request.seq, tcs);
      }

      SendMessage(request);

      return tcs.Task;
    }


    protected abstract void DispatchRequest(int reqSeq, string command, JToken args);


    private void ProcessData()
    {
      while (true)
      {
        if (_bodyLength >= 0)
        {
          if (_rawData.Length >= _bodyLength)
          {
            var buf = _rawData.RemoveFirst(_bodyLength);
            string data = Encoding.UTF8.GetString(buf);
            Logger.LogTrace("received data: Content-Length: ({0})rnrn{{{1}}}", _bodyLength, data);
            Dispatch(data);
            _bodyLength = -1;
            continue; // there may be more complete messages to process
          }
          else  // currently held raw data is insufficient to completely re-construct the body
          {
            break;
          }
        }
        else // (_bodyLength == -1) means we got a new message (i.e., a message with Content-Length: (\d+): \r\n\r\n{body})
        {
          string s = _rawData.GetString();

          if (string.IsNullOrWhiteSpace(s))
          {
            _rawData.RemoveFirst(s.Length);
            break;
          }

          Match m = CONTENT_LENGTH_MATCHER.Match(s);
          if (m.Success && m.Groups.Count == 2)
          {
            _bodyLength = Convert.ToInt32(m.Groups[1].ToString());
            _rawData.RemoveFirst(m.Index + "Content-Length: ".Length + m.Groups[1].Length + 4);
            continue; // try to handle a complete message
          }
          else
          {
            // TODO: do proper exit strategy here
            Logger.LogWarn(@"could not regex 'Content-Length: (\d+)' in: {0}", s);
          }
        }

        break;
      }
    }

    private void Dispatch(string req)
    {
      var message = JsonConvert.DeserializeObject<ProtocolMessage>(req);
      if (message == null)
      {
        Logger.LogError("could not deserialize provided request into a ProtocolMessage: {0}", req);
        return;
      }
      switch (message.type)
      {
        case "request":
          {
            var request = JObject.Parse(req);
            var reqSeq = (int)request["seq"];
            var cmd = (string)request["command"];
            var args = request["arguments"];
            DispatchRequest(reqSeq, cmd, args);
          }
          break;

        case "response":
          {
            var response = JsonConvert.DeserializeObject<Response>(req);
            int seq = response.request_seq;
            lock (_pendingRequests)
            {
              if (_pendingRequests.ContainsKey(seq))
              {
                var tcs = _pendingRequests[seq];
                _pendingRequests.Remove(seq);
                tcs.SetResult(response);
              }
            }
          }
          break;
        case "event":
          // we don't care about events for the moment
          break;
        default:
          Logger.LogWarn("unsupported message type: {0}", message.type);
          break;
      }
    }


    protected void SendMessage(ProtocolMessage message)
    {
      if (message.seq == 0)
        message.seq = _sequenceNumber++;

      var data = ConvertToBytes(message);
      try
      {
        _outputStream.Write(data, 0, data.Length);
        _outputStream.Flush();
      }
      catch (Exception e)
      {
        Logger.LogError("{0} {1}", e.Message, e.StackTrace);
      }

      Logger.LogTrace("sent {0}: {1}", message.type, message);
    }

    private static byte[] ConvertToBytes(ProtocolMessage request)
    {
      var asJson = JsonConvert.SerializeObject(request);
      byte[] jsonBytes = Encoding.UTF8.GetBytes(asJson);

      string header = string.Format($"Content-Length: {jsonBytes.Length}\r\n\r\n");
      byte[] headerBytes = Encoding.UTF8.GetBytes(header);

      byte[] data = new byte[headerBytes.Length + jsonBytes.Length];
      Buffer.BlockCopy(headerBytes, 0, data, 0, headerBytes.Length);
      Buffer.BlockCopy(jsonBytes, 0, data, headerBytes.Length, jsonBytes.Length);

      return data;
    }
  }


  /// <summary> Encapsulates a byte array (akin to a bytebuffer in Python). </summary>
  class ByteBuffer
  {
    private byte[] _buffer = Array.Empty<byte>();

    public int Length => _buffer.Length;

    public string GetString() => Encoding.UTF8.GetString(_buffer);


    /// <summary>
    /// Pops a string from internal array [0, <paramref name="length">[.
    /// </summary>
    /// <param name="length"></param>
    /// <returns></returns>
    public string PopString(int _)
    {
      return string.Empty;
    }

    // TODO: replace this fuckin garbage of a mess
    public void Append(byte[] b, int length)
    {
      byte[] newBuffer = new byte[_buffer.Length + length];
      Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _buffer.Length);
      Buffer.BlockCopy(b, 0, newBuffer, _buffer.Length, length);
      _buffer = newBuffer;
    }

    public byte[] RemoveFirst(int n)
    {
      byte[] b = new byte[n];
      Buffer.BlockCopy(_buffer, 0, b, 0, n);
      byte[] newBuffer = new byte[_buffer.Length - n];
      Buffer.BlockCopy(_buffer, n, newBuffer, 0, _buffer.Length - n);
      _buffer = newBuffer;
      return b;
    }
  }
}
