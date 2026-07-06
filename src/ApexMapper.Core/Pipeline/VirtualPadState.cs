namespace ApexMapper.Core.Pipeline;

/// <summary>
/// The virtual pad's desired state, in normalized units. Sticks are -1..1 per
/// axis using the XInput sign convention: positive X = right, positive Y = up
/// (forward). A binding's positive key therefore steers right / pushes up.
/// Triggers are 0..1. Signs are preserved end-to-end into the driver report;
/// in-game direction remains pending real-hardware validation.
/// </summary>
public struct VirtualPadState
{
    public float LeftStickX;
    public float LeftStickY;
    public float RightStickX;
    public float RightStickY;
    public float LeftTrigger;
    public float RightTrigger;
    public bool ButtonA;
    public bool ButtonB;
    public bool ButtonX;
    public bool ButtonY;
    public bool ButtonLB;
    public bool ButtonRB;
    public bool ButtonStart;
    public bool ButtonBack;
    public bool ButtonLS;
    public bool ButtonRS;
    public bool ButtonGuide;
    public bool DpadUp;
    public bool DpadDown;
    public bool DpadLeft;
    public bool DpadRight;

    public void Reset()
    {
        LeftStickX = 0f; LeftStickY = 0f;
        RightStickX = 0f; RightStickY = 0f;
        LeftTrigger = 0f; RightTrigger = 0f;
        ButtonA = false; ButtonB = false; ButtonX = false; ButtonY = false;
        ButtonLB = false; ButtonRB = false;
        ButtonStart = false; ButtonBack = false;
        ButtonLS = false; ButtonRS = false; ButtonGuide = false;
        DpadUp = false; DpadDown = false; DpadLeft = false; DpadRight = false;
    }
}
