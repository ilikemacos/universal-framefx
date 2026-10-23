using UniversalFrameFX.Agent;

namespace UniversalFrameFX.Agent.App;

/// <summary>
/// Yes/No gate before opening LarkquillAI (cloud panel or local agent).
/// Answer is persisted under the agent root; unset asks once on first open.
/// </summary>
public static class LarkquillPermission
{
    public const string Question = "Do we have permission to run LarkquillAI locally for FrameFX purposes?";
    public const string UnavailableMessage = "LarkquillAI is unavailable.";

    /// <summary>Cached read of the persisted answer; null when the user has not answered yet.</summary>
    public static bool? Allowed => LarkquillPermissionStore.Load(AgentPaths.Default().Root).Allowed;

    /// <summary>
    /// Returns true if LarkquillAI may open. When unset, shows the Yes/No question and saves the answer.
    /// When already No (or the user picks No), shows that LarkquillAI is unavailable and returns false.
    /// </summary>
    public static bool EnsureAllowed(IWin32Window? owner)
    {
        string root = AgentPaths.Default().Root;
        var state = LarkquillPermissionStore.Load(root);
        if (state.Allowed == true) return true;
        if (state.Allowed == false)
        {
            MessageBox.Show(owner, UnavailableMessage, "LarkquillAI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var answer = MessageBox.Show(
            owner,
            Question,
            "LarkquillAI",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        bool yes = answer == DialogResult.Yes;
        LarkquillPermissionStore.Save(root, new LarkquillPermissionStore.State { Allowed = yes });
        if (!yes)
            MessageBox.Show(owner, UnavailableMessage, "LarkquillAI", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return yes;
    }
}
