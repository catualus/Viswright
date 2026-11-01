using System;
using System.Collections.Generic;
using System.Linq;
using SourceKit.Vmf;

namespace Viswright
{
    /// <summary>
    /// The check that a run changed nothing it was not allowed to.
    ///
    /// Each pass declares the kinds of change it makes. After every pass has run, the optimised map is
    /// compared with the one that was read, and any difference no pass declared stops the run: a
    /// mistake in a pass becomes an error in the log, never a quietly different map.
    /// </summary>
    public sealed class ChangeGuard
    {
        private readonly List<(string Owner, Func<VmfChange, bool> Allows)> rules = [];

        /// <summary>Moving the map rewrites each func_instance's file; nothing else about it changes.</summary>
        public static ChangeGuard ForRelocation()
        {
            var guard = new ChangeGuard();
            guard.Allow("relocation", change =>
                change.Kind == VmfChangeKind.ValueChanged
                && change.Path.StartsWith("entity#", StringComparison.Ordinal)
                && change.Path.EndsWith("/file", StringComparison.Ordinal));
            return guard;
        }

        public void Allow(string owner, Func<VmfChange, bool> allows) => rules.Add((owner, allows));

        /// <summary>The changes no rule allows, which is an empty list when the run kept its promise.</summary>
        public IReadOnlyList<VmfChange> Undeclared(IEnumerable<VmfChange> changes) =>
            changes.Where(change => !rules.Any(rule => rule.Allows(change))).ToList();
    }
}
