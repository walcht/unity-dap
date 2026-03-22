using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace UnityDebugAdapter
{
  public abstract class DebugSession : ProtocolServer
  {
    protected bool _clientLinesStartAt1 = true;
    protected bool _clientPathsAreURI = true;

    public DebugSession() { }

    public void SendErrorResponse(int requestSequence, string command, int id, string format, Dictionary<string, string> variables = null, bool user = true, bool telemetry = false)
    {
      var response = new Response()
      {
        command = command,
        request_seq = requestSequence,
      };
      if (string.IsNullOrEmpty(format))
        throw new ArgumentException($"'{nameof(format)}' cannot be null or empty.", nameof(format));

      var msg = new Message(id, format, variables, user, telemetry);
      // TODO: format string somehow without using Reflection or dynamic
      response.SetErrorBody("HEHHEHEHEHEHEHE", new ErrorResponseBody(msg));
      SendMessage(response);
    }

    protected override void DispatchRequest(int reqSeq, string command, JToken args)
    {
      try
      {
        switch (command)
        {
          case "initialize":
            Initialize(reqSeq, args);
            break;

          // done
          case "launch":
            Launch(reqSeq, args);
            break;

          // done
          case "attach":
            Attach(reqSeq, args);
            break;

          // done
          case "disconnect":
            Disconnect(reqSeq, args);
            break;

          // done
          case "next":
            Next(reqSeq, args);
            break;

          // done
          case "continue":
            Continue(reqSeq, args);
            break;

          // done
          case "stepIn":
            StepIn(reqSeq, args);
            break;

          // done
          case "stepOut":
            StepOut(reqSeq, args);
            break;

          // done
          case "pause":
            Pause(reqSeq, args);
            break;

          // done
          case "stackTrace":
            StackTrace(reqSeq, args);
            break;

          // done
          case "scopes":
            Scopes(reqSeq, args);
            break;

          // done
          case "variables":
            Variables(reqSeq, args);
            break;

          // done
          case "source":
            Source(reqSeq, args);
            break;

          // done
          case "threads":
            Threads(reqSeq, args);
            break;

          // done
          case "setBreakpoints":
            SetBreakpoints(reqSeq, args);
            break;

          // done
          case "setFunctionBreakpoints":
            SetFunctionBreakpoints(reqSeq, args);
            break;

          // done
          case "setExceptionBreakpoints":
            SetExceptionBreakpoints(reqSeq, args);
            break;

          // done
          case "evaluate":
            Evaluate(reqSeq, args);
            break;

          // done
          case "setVariable":
            SetVariable(reqSeq, args);
            break;

          default:
            SendErrorResponse(reqSeq, command, 1014, "unrecognized request: {_request}",
                new Dictionary<string, string> { { "_request", command } });
            break;
        }
      }
      catch (Exception e)
      {
        SendErrorResponse(reqSeq, command, 1104, "error while processing request '{_request}' (exception: {_exception})",
            new Dictionary<string, string> { { "_request", command }, { "_exception", e.Message } });
      }

      if (command == "disconnect")
      {
        Stop();
      }
    }

    protected abstract void SetVariable(int reqSeq, JToken args);

    public abstract void Initialize(int reqSeq, JToken args);

    public abstract void Launch(int reqSeq, JToken args);

    public abstract void Attach(int reqSeq, JToken args);

    public abstract void Disconnect(int reqSeq, JToken args);

    public abstract void SetFunctionBreakpoints(int reqSeq, JToken args);

    public abstract void SetExceptionBreakpoints(int reqSeq, JToken args);

    public abstract void SetBreakpoints(int reqSeq, JToken args);

    public abstract void Continue(int reqSeq, JToken args);

    public abstract void Next(int reqSeq, JToken args);

    public abstract void StepIn(int reqSeq, JToken args);

    public abstract void StepOut(int reqSeq, JToken args);

    public abstract void Pause(int reqSeq, JToken args);

    public abstract void StackTrace(int reqSeq, JToken args);

    public abstract void Scopes(int reqSeq, JToken args);

    public abstract void Variables(int reqSeq, JToken args);

    public abstract void Source(int reqSeq, JToken args);

    public abstract void Threads(int reqSeq, JToken args);

    public abstract void Evaluate(int reqSeq, JToken args);


    protected int ConvertDebuggerLineToClient(int line)
    {
      return _clientLinesStartAt1 ? line : line - 1;
    }

    protected int ConvertClientLineToDebugger(int line)
    {
      return _clientLinesStartAt1 ? line : line + 1;
    }

    protected string ConvertDebuggerPathToClient(string path)
    {
      if (_clientPathsAreURI)
      {
        try
        {
          var uri = new Uri(path);
          return uri.AbsoluteUri;
        }
        catch
        {
          return null;
        }
      }
      else
      {
        return path;
      }
    }

    protected string ConvertClientPathToDebugger(string clientPath)
    {
      if (clientPath == null)
        return null;

      if (!_clientPathsAreURI)
        return clientPath;

      if (Uri.IsWellFormedUriString(clientPath, UriKind.Absolute))
      {
        var uri = new Uri(clientPath);
        return uri.LocalPath;
      }

      Logger.LogError($"path not well formed: '{clientPath}'");
      return null;
    }
  }
}
