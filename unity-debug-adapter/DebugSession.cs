using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace UnityDebugAdapter
{
  public abstract class DebugSession : ProtocolServer
  {
    protected static readonly Regex VARIABLE_REGEX = new Regex(@"\{_(\w+)\}");
    protected bool _clientLinesStartAt1 = true;
    protected bool _clientPathsAreURI = true;

    public DebugSession() { }

    public void SendErrorResponse(int requestSequence, string command, int id, string format,
        Dictionary<string, string> variables = null, bool user = true, bool telemetry = false)
    {
      format ??= "";
      variables ??= new Dictionary<string, string>();
      var response = new Response()
      {
        command = command,
        request_seq = requestSequence,
        success = false, // this is set in SetErrorBody (but also just set it here for readability)
      };
      var msg = new Message(id, format, variables, user, telemetry);
      string msg_str = VARIABLE_REGEX.Replace(format, m =>
      {
        if (variables.TryGetValue(m.Groups[1].Value, out string replacement))
          return replacement;
        return $"{{{m.Groups[1].Value}}}: not found";
      });

      response.SetErrorBody(msg_str, new ErrorResponseBody(msg));
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

          case "launch":
            Launch(reqSeq, args);
            break;

          case "attach":
            Attach(reqSeq, args);
            break;

          case "disconnect":
            Disconnect(reqSeq, args);
            break;

          case "next":
            Next(reqSeq, args);
            break;

          case "continue":
            Continue(reqSeq, args);
            break;

          case "stepIn":
            StepIn(reqSeq, args);
            break;

          case "stepOut":
            StepOut(reqSeq, args);
            break;

          case "pause":
            Pause(reqSeq, args);
            break;

          case "stackTrace":
            StackTrace(reqSeq, args);
            break;

          case "scopes":
            Scopes(reqSeq, args);
            break;

          case "variables":
            Variables(reqSeq, args);
            break;

          case "source":
            Source(reqSeq, args);
            break;

          case "threads":
            Threads(reqSeq, args);
            break;

          case "setBreakpoints":
            SetBreakpoints(reqSeq, args);
            break;

          case "setFunctionBreakpoints":
            SetFunctionBreakpoints(reqSeq, args);
            break;

          case "setExceptionBreakpoints":
            SetExceptionBreakpoints(reqSeq, args);
            break;

          case "evaluate":
            Evaluate(reqSeq, args);
            break;

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
  }
}
