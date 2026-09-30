namespace KnowledgeCapture.Core.Services;

/// <summary>
/// Serializes calls to the local model (one NPU, one request at a time). Interactive calls (the chat stream the
/// employee is watching) jump ahead of queued background calls (coverage, anonymization, summaries).
/// </summary>
public sealed class LlmGate
{
    private readonly object _lock = new();
    private readonly LinkedList<TaskCompletionSource<bool>> _interactive = new();
    private readonly LinkedList<TaskCompletionSource<bool>> _background = new();
    private bool _busy;

    public async Task<IDisposable> EnterAsync(bool interactive, CancellationToken ct)
    {
        TaskCompletionSource<bool> tcs;
        LinkedListNode<TaskCompletionSource<bool>> node;
        lock (_lock)
        {
            if (!_busy) { _busy = true; return new Releaser(this); }
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = (interactive ? _interactive : _background).AddLast(tcs);
        }
        using (ct.Register(() =>
               {
                   lock (_lock)
                   {
                       if (node.List is null) return; // already granted
                       node.List.Remove(node);
                   }
                   tcs.TrySetCanceled(ct);
               }))
        {
            await tcs.Task.ConfigureAwait(false);
        }
        return new Releaser(this);
    }

    private void Release()
    {
        TaskCompletionSource<bool>? next = null;
        lock (_lock)
        {
            var q = _interactive.Count > 0 ? _interactive : _background.Count > 0 ? _background : null;
            if (q is null) _busy = false;
            else { next = q.First!.Value; q.RemoveFirst(); }
        }
        next?.TrySetResult(true);
    }

    private sealed class Releaser(LlmGate gate) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release(); }
    }
}
