using UnityEngine;
using UnityEngine.EventSystems;

namespace QuinnBast.Shapez2.ModReloader;

/// <summary>
/// Keeps the crash screen clickable.
///
/// The crash overlay survives a fatal exception because it belongs to <c>GameView</c>, which
/// lives for the process. The thing that makes it *clickable* does not: every sub-orchestrator
/// owns its own EventSystem - <c>GameSessionOrchestrator.MainEventSystem</c>,
/// <c>MainMenuOrchestrator.MainEventSystem</c>, <c>IntroOrchestrator.EventSystem</c> - and
/// releases it when disposed. <c>HandleFatalException</c> shows the screen and then awaits
/// <c>TryDisposing()</c>, so moments later the EventSystem is gone and no button on that
/// screen can be pressed, the game's own Copy to Clipboard and Report on Discord included.
///
/// It only *sometimes* looks broken because <c>TryDisposing</c> swallows whatever the
/// teardown throws: a dispose that fails partway leaves the EventSystem alive and every
/// button working, which is why the same screen behaves differently depending on how hard
/// the game fell over.
///
/// So this watches for the gap and fills it. It has to keep watching rather than check once,
/// because at the moment the screen is put up the doomed EventSystem is still there.
/// </summary>
public class CrashScreenInput : MonoBehaviour
{
    private EventSystem Ours;

    /// <summary>
    /// Starts watching, on an object that outlives the scene changes a reload causes.
    /// </summary>
    public static CrashScreenInput Attach()
    {
        GameObject host = new GameObject("ModReloaderCrashInput");
        DontDestroyOnLoad(host);

        return host.AddComponent<CrashScreenInput>();
    }

    /// <summary>
    /// Stops watching and takes any EventSystem this created with it.
    ///
    /// Called when the crash screen is dismissed, so that the main menu's own EventSystem is
    /// the only one once the game is running again - two enabled at once is a state Unity
    /// warns about and resolves arbitrarily.
    /// </summary>
    public void Detach()
    {
        if (this != null)
        {
            Destroy(gameObject);
        }
    }

    private void Update()
    {
        // Someone else's EventSystem is in charge, which is the normal case and the desired
        // one - ours exists only to cover the window where there is none at all.
        if (EventSystem.current != null && EventSystem.current != Ours)
        {
            return;
        }

        if (Ours != null)
        {
            return;
        }

        Ours = gameObject.AddComponent<EventSystem>();

        // Without an input module the EventSystem raises no pointer events, so the screen
        // would still be dead. StandaloneInputModule is the one that reads mouse and
        // keyboard from the legacy input system, which is what the game's own prefab uses.
        gameObject.AddComponent<StandaloneInputModule>();
    }
}
