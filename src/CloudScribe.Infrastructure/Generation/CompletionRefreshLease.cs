using System.Text;

namespace CloudScribe.Infrastructure.Generation;

/// <summary>Only the gate can create a live lease; inherited async state cannot renew it.</summary>
public sealed class CompletionRefreshLease
{
    private readonly CompletionWorkflowGate _owner;
    private readonly CompletionGateConfiguration _configuration;
    private readonly System.Threading.Lock _sync = new();
    private int _closed;
    private int _catalogRequests;
    private int _probeRequests;
    private int _catalogInFlight;
    private int _probeInFlight;
    private string? _catalog;
    private string? _probe;
    private bool _persisted;

    internal CompletionRefreshLease(CompletionWorkflowGate owner, CompletionGateConfiguration configuration, Guid id, string identity)
    { _owner = owner; _configuration = configuration; Id = id; Identity = identity; }

    public Guid Id { get; }
    internal string Identity { get; }
    internal string EvidenceHash => CompletionWorkflowGate.Hash(Encoding.UTF8.GetBytes(_catalog + "|" + _probe + "|" + Id.ToString("D")));

    internal void Require(CompletionWorkflowGate owner, CompletionGateConfiguration current, Guid? id)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_owner, owner) || Volatile.Read(ref _closed) != 0 || current != _configuration
                || (id is { } expected && expected != Id))
                throw new InvalidOperationException("Completion refresh lease changed, expired or already closed.");
        }
    }

    public void CheckBeforeEffect() => _owner.Check(this);

    internal void Request(bool probe)
    {
        CheckBeforeEffect();
        lock (_sync)
        {
            if (_closed != 0) throw new InvalidOperationException("Completion refresh is already closed.");
            int count = probe ? Interlocked.Increment(ref _probeRequests) : Interlocked.Increment(ref _catalogRequests);
            // Full UI cycle: bootstrap catalog, fixed empty probe, then trusted UI query catalog.
            if (count > (probe ? 1 : 2)) throw new InvalidOperationException("Completion refresh request-stage limit exceeded.");
            if (probe) _probeInFlight++; else _catalogInFlight++;
        }
    }

    internal void CatalogObserved(GoogleVoiceCatalogSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_closed != 0 || _catalogInFlight <= 0) throw new InvalidOperationException("Catalog response has no live pending stage.");
            if (!string.Equals(snapshot.Account.AccountId, _configuration.AccountId, StringComparison.Ordinal)
                || !string.Equals(snapshot.Endpoint.AbsoluteUri, _configuration.CatalogEndpoint, StringComparison.Ordinal)
                || !snapshot.Voices.Any(voice => string.Equals(voice.Name, _configuration.VoiceName, StringComparison.Ordinal)
                    && voice.LanguageCodes.Contains(_configuration.LanguageCode, StringComparer.Ordinal)))
                throw new InvalidOperationException("Observed catalog does not match the admitted completion connection.");
            if (_catalog is not null && !string.Equals(_catalog, snapshot.ProvenanceId, StringComparison.Ordinal))
                throw new InvalidOperationException("Catalog changed within one completion refresh cycle.");
            _catalog = snapshot.ProvenanceId;
            _catalogInFlight--;
        }
    }

    internal void ProbeObserved(string project, GoogleSynthesisCapabilityEvidence evidence)
    {
        lock (_sync)
        {
            if (_closed != 0 || _probeInFlight != 1) throw new InvalidOperationException("Probe response has no live pending stage.");
            if (!string.Equals(project, _configuration.ProjectId, StringComparison.Ordinal) || _probe is not null
                || !evidence.ProvenanceId.StartsWith("google-synthesis-probe:invalid-argument:sha256:", StringComparison.Ordinal))
                throw new InvalidOperationException("Observed empty probe does not match this completion cycle.");
            _probe = evidence.ProvenanceId;
            _probeInFlight--;
        }
    }

    internal void PersistenceObserved(GoogleTextToSpeechCatalogBootstrapResult result)
    {
        lock (_sync)
        {
            if (_closed != 0) throw new InvalidOperationException("Completion refresh is already closed.");
            if (!string.Equals(result.Account.Reference.AccountId, _configuration.AccountId, StringComparison.Ordinal) || !string.Equals(result.ProjectId, _configuration.ProjectId, StringComparison.Ordinal)
                || !string.Equals(result.CatalogProvenanceId, _catalog, StringComparison.Ordinal) || _probe is null)
                throw new InvalidOperationException("Persisted refresh evidence does not match its observed completion cycle.");
            _persisted = true;
        }
    }

    internal void Finish(bool success)
    {
        lock (_sync)
        {
            try
            {
                bool verified = success && _persisted && _catalog is not null && _probe is not null
                    && _catalogInFlight == 0 && _probeInFlight == 0
                    && Volatile.Read(ref _catalogRequests) is >= 1 and <= 2 && Volatile.Read(ref _probeRequests) == 1;
                _owner.Finish(this, verified);
                if (success && !verified) throw new CompletionGateException(CompletionGateFailure.Stopped);
            }
            finally { Interlocked.Exchange(ref _closed, 1); }
        }
    }
}
