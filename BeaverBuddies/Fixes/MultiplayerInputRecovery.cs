using Timberborn.InputSystem;
using Timberborn.KeyBindingSystem;
using Timberborn.SingletonSystem;

namespace BeaverBuddies.Fixes
{
    // Bound only in multiplayer game scenes. Input devices outlive scene reloads.
    // Queue recovery for Update instead of resetting devices inside replay/input
    // callbacks. Multiple requests in the same frame require just one reset.
    public class MultiplayerInputRecovery : IPostLoadableSingleton, IUpdatableSingleton
    {
        private readonly IInputStateResetter _resetter;
        private readonly KeyBindingRegistry _bindings;
        private bool _pending;

        public MultiplayerInputRecovery(IInputStateResetter resetter, KeyBindingRegistry bindings)
        {
            _resetter = resetter;
            _bindings = bindings;
        }

        public void RequestReset() => _pending = true;

        public void PostLoad() => RequestReset();

        public void UpdateSingleton()
        {
            if (!_pending) return;
            _pending = false;
            _resetter.ResetInputState();
            foreach (var binding in _bindings.KeyBindings)
            {
                // Lock clears cached down/held state. A second call consumes the
                // synthetic release from clearing a held key, so a stale mouse
                // release cannot cancel a dialog or commit a tool action.
                // Normal unpressed input updates unlock the binding again.
                binding.Lock();
                binding.Lock();
            }
        }
    }
}
