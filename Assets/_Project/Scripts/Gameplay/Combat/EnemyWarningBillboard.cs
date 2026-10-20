using UnityEngine;

/// <summary>Optional presentation component. Remove for art that has its own billboard.</summary>
public sealed class EnemyWarningBillboard : MonoBehaviour
{
    private void LateUpdate()
    {
        if (Camera.main != null) transform.rotation = Camera.main.transform.rotation;
    }
}
