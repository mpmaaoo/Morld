using System;
using Result = EnemyDecisionTree.Result;

public static class EnemyDecisionTreeRegression
{
#if UNITY_EDITOR
    [UnityEditor.MenuItem("Morld/Tests/Run Enemy Decision Tree Regression")]
    private static void MenuRun() { Run(); UnityEngine.Debug.Log("[Morld] Behaviour tree priority/preemption tests passed."); }
#endif
    public static void Run()
    {
        bool returning = false, visible = false, investigating = false;
        string branch = "";
        var tree = new EnemyDecisionTree.Selector(
            new EnemyDecisionTree.Sequence(new EnemyDecisionTree.Condition(() => returning), new EnemyDecisionTree.Action(() => { branch = "return"; return Result.Running; })),
            new EnemyDecisionTree.Sequence(new EnemyDecisionTree.Condition(() => visible), new EnemyDecisionTree.Action(() => { branch = "chase"; return Result.Running; })),
            new EnemyDecisionTree.Sequence(new EnemyDecisionTree.Condition(() => investigating), new EnemyDecisionTree.Action(() => { branch = "search"; return Result.Running; })),
            new EnemyDecisionTree.Action(() => { branch = "patrol"; return Result.Running; }));
        tree.Tick(); Require(branch == "patrol", "patrol fallback");
        investigating = true; tree.Tick(); Require(branch == "search", "last-seen search");
        visible = true; tree.Tick(); Require(branch == "chase", "reacquisition interrupts search");
        returning = true; tree.Tick(); Require(branch == "return", "return preempts pursuit");
        tree.Tick(); Require(branch == "return", "visible player cannot interrupt return");
        returning = visible = investigating = false;
        tree.Tick(); Require(branch == "patrol", "resumes patrol after return");
        Require(!EnemyPursuitRules.ShouldLock(false, false, true, 1f, 6f), "unnoticed player behind enemy must not trigger lock");
        Require(!EnemyPursuitRules.ShouldLock(false, true, true, 7f, 6f), "discovered player outside lock radius");
        Require(EnemyPursuitRules.ShouldLock(false, true, true, 4f, 6f), "detected player enters lock radius");
        Require(EnemyPursuitRules.ShouldLock(true, false, true, 4f, 6f), "lost sight inside radius preserves pursuit");
        Require(EnemyPursuitRules.ShouldLock(true, false, true, 6f, 6f), "radius boundary remains locked");
        Require(!EnemyPursuitRules.ShouldLock(true, true, true, 6.01f, 6f), "player escapes radius");
        Require(!EnemyPursuitRules.ShouldLock(true, true, false, 1f, 6f), "dead target releases lock");
        Require(!EnemyPursuitRules.ShouldLock(true, true, true, 0f, 0f), "zero radius disables lock");
        Require(!EnemyPursuitRules.ShouldPrioritizeDepth(3f, 3f), "exact depth boundary allows attack");
        Require(EnemyPursuitRules.ShouldPrioritizeDepth(3.01f, 3f), "greater depth requires pursuit");
        Require(EnemyPursuitRules.ShouldPrioritizeDepth(-3.01f, 3f), "opposite depth requires pursuit");
        Require(!EnemyPursuitRules.ShouldPrioritizeDepth(0.5f, 3f), "near depth allows normal attack");
    }
    private static void Require(bool condition, string description)
    { if (!condition) throw new Exception("Enemy decision regression: " + description); }
}
