using UnityEngine;

/// <summary>
/// Optional prefab strategy for future chase/ranged/special enemies.
/// Called only while alive and outside camera rotation. Root motion must use TryMoveOnGround.
/// PerformAttack returns true when this strategy handles the committed strike itself.
/// </summary>
public abstract class EnemyBehaviour : MonoBehaviour
{
    public virtual bool AllowsAttack => true;
    public virtual bool OwnsLookDirection => false;
    public virtual bool CanAttackWithoutSight => false;
    public virtual bool AllowsProximityAttack => AllowsAttack;
    public virtual void Initialize(StationaryMeleeEnemy enemy) { }
    public virtual void Tick(StationaryMeleeEnemy enemy, float deltaTime) { }
    public virtual bool PerformAttack(StationaryMeleeEnemy enemy, Health target) { return false; }
}
