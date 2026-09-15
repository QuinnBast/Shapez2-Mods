using UnityEngine;
using UnityEngine.UI;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// Four little bars around the middle of the screen.
///
/// Deliberately uGUI rather than IMGUI. IMGUI needs an <c>OnGUI</c>, which needs a
/// MonoBehaviour defined in this assembly - and Mod Reloader byte-loads a rebuilt
/// assembly, after which Unity refuses to add a component whose type came from it and
/// <c>AddComponent</c> quietly returns null. <c>Canvas</c> and <c>Image</c> are Unity's own
/// types, so this survives a hot reload and needs no fallback.
///
/// Nothing here is a raycast target: the crosshair must not eat the click it is aiming.
/// </summary>
public sealed class Crosshair
{
    private static readonly Color Idle = new Color(1f, 1f, 1f, 0.35f);
    private static readonly Color Targeted = new Color(1f, 0.82f, 0.4f, 0.95f);

    private GameObject Root;
    private Image[] Arms;
    private bool LastTargeted;

    public void Show()
    {
        if (Root != null)
        {
            Root.SetActive(true);
            return;
        }

        Root = new GameObject("FirstPersonCrosshair");
        Object.DontDestroyOnLoad(Root);

        Canvas canvas = Root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Above the game's HUD, below the range mods use for full-screen input blockers.
        canvas.sortingOrder = 25000;

        float arm = FirstPersonTuning.CrosshairArm;
        float gap = FirstPersonTuning.CrosshairGap;
        float thickness = FirstPersonTuning.CrosshairThickness;
        float offset = gap + arm * 0.5f;

        Arms = new[]
        {
            Arm(new Vector2(thickness, arm), new Vector2(0f, offset)),
            Arm(new Vector2(thickness, arm), new Vector2(0f, -offset)),
            Arm(new Vector2(arm, thickness), new Vector2(-offset, 0f)),
            Arm(new Vector2(arm, thickness), new Vector2(offset, 0f)),
        };

        Apply(Idle);
        LastTargeted = false;
    }

    public void SetTargeted(bool targeted)
    {
        if (Arms == null || targeted == LastTargeted)
        {
            return;
        }

        LastTargeted = targeted;
        Apply(targeted ? Targeted : Idle);
    }

    public void Hide()
    {
        if (Root != null)
        {
            Root.SetActive(false);
        }
    }

    /// <summary>
    /// Destroys the canvas outright rather than hiding it. A hot reload builds a second
    /// one, and two crosshairs are only distinguishable by being slightly too bright.
    /// </summary>
    public void Dispose()
    {
        if (Root != null)
        {
            Object.Destroy(Root);
            Root = null;
            Arms = null;
        }
    }

    private Image Arm(Vector2 size, Vector2 offset)
    {
        GameObject bar = new GameObject("Arm", typeof(RectTransform));
        bar.transform.SetParent(Root.transform, false);

        Image image = bar.AddComponent<Image>();
        image.raycastTarget = false;

        RectTransform rect = image.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = offset;

        return image;
    }

    private void Apply(Color color)
    {
        foreach (Image arm in Arms)
        {
            if (arm != null)
            {
                arm.color = color;
            }
        }
    }
}
