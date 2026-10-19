using UnityEngine;

/// <summary>
/// 左上角說明用的深色底，避免淺色場景裡白字看不見。
/// </summary>
public static class ScreenHud
{
    private static GUIStyle _style;

    public static void Draw(Rect rect, string text)
    {
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(10, 10, 8, 8)
            };
            _style.normal.textColor = new Color(0.96f, 0.95f, 0.9f);
        }

        var previous = GUI.color;
        GUI.color = new Color(0.06f, 0.07f, 0.09f, 0.88f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
        GUI.Label(rect, text, _style);
    }
}
