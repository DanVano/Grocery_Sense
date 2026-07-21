namespace GrocerySense.App.Services;

// Session-only UI state: keeps Shop Mode on when the user taps away to another bottom-nav
// destination and back. Blazor re-inits the Shopping List page on every visit, so without this
// the toggle would reset to off each time. Deliberately not persisted to disk — a fresh app
// launch starts out of Shop Mode. Singleton.
// ponytail: a bare bool is enough; grow to a record only if the shop "session" needs more state.
public sealed class ShopModeState
{
    public bool Enabled { get; set; }
}
