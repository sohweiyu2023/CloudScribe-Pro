namespace CloudScribe.Domain.Safety;

public static class BackupRestoreAdmissionPolicy
{
    public static BackupRestoreDecision Evaluate(
        bool archiveStructureValid,
        bool manifestAuthenticated,
        bool schemaSupported,
        bool secretsExcluded,
        bool nativePayloadsAllowed,
        bool pathTraversalSafe)
    {
        if (!archiveStructureValid) return new(false, "restore-archive-invalid");
        if (!manifestAuthenticated) return new(false, "restore-manifest-not-authenticated");
        if (!schemaSupported) return new(false, "restore-schema-unsupported");
        if (!secretsExcluded) return new(false, "restore-secret-material-present");
        if (!nativePayloadsAllowed) return new(false, "restore-native-payload-policy-denied");
        if (!pathTraversalSafe) return new(false, "restore-path-traversal-risk");
        return new(true, "restore-admitted");
    }
}
