using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
namespace NexoRuta.IntegrationTests.Fixtures;
internal sealed class AuthDecisionCapture:ILoggerProvider
{
    internal ConcurrentQueue<Dictionary<string,object?>> Rows {get;}=new();
    public ILogger CreateLogger(string categoryName)=>new Sink(this);
    internal ILogger<T> For<T>() => new Typed<T>(this);
    private sealed class Typed<T>(AuthDecisionCapture owner):ILogger<T>
    {
        private readonly ILogger sink=owner.CreateLogger(typeof(T).FullName!);
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>sink.Log(level,id,state,exception,formatter);
    }
    public void Dispose() { }
    private sealed class Sink(AuthDecisionCapture owner):ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {
            if(id.Name!="AuthenticationDecision"||state is not IEnumerable<KeyValuePair<string,object?>> fields)return;
            var allowed=new[]{"Status","Reason","AuthTrace","ServerUtc","Method","Route"};
            owner.Rows.Enqueue(fields.Where(p=>allowed.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value));
        }
    }
}
