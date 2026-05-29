using System;
using System.IO;
using System.Linq;

namespace RevitCli.Release;

internal static class OfficePilotEvidenceGuard
{
    private static readonly string[] NonOfficePilotEvidenceMarkers =
    {
        "synthetic CLI fixture",
        "not evidence from a real office project-copy pilot",
        "not office rollout evidence",
        "not office rollout completion and not a production support claim",
        "local controlled pilot packet",
    };

    public static bool IsOfficePilotEvidenceCandidate(
        string? pilotId,
        string? evidencePacketPath,
        string? packet) =>
        !TryDescribeNonOfficePilotEvidence(pilotId, evidencePacketPath, packet, out _);

    public static bool TryDescribeNonOfficePilotEvidence(
        string? pilotId,
        string? evidencePacketPath,
        string? packet,
        out string reason)
    {
        if (ContainsNonOfficePilotToken(pilotId))
        {
            reason = "Pilot id is reserved for synthetic or local-controlled evidence and cannot count as a completed office rollout pilot.";
            return true;
        }

        if (ContainsNonOfficePilotToken(evidencePacketPath) ||
            string.Equals(Path.GetFileName(evidencePacketPath ?? ""), "release-pilot-spine.md", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Evidence packet path points at synthetic or local-controlled evidence and cannot count as a completed office rollout pilot.";
            return true;
        }

        foreach (var marker in NonOfficePilotEvidenceMarkers)
        {
            if (packet?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true)
            {
                reason = $"Evidence packet declares '{marker}' and cannot count as a completed office rollout pilot.";
                return true;
            }
        }

        reason = "";
        return false;
    }

    private static bool ContainsNonOfficePilotToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains("synthetic", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("local-controlled", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("local controlled", StringComparison.OrdinalIgnoreCase);
    }
}
