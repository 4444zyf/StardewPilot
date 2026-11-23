using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace StardewPilot.Guide;

/// <summary>Shows the latest copilot message in a corner of the HUD for a few seconds.</summary>
internal static class Toast
{
    private static ChatEntry? current;
    private static double shownAt;
    private const double Seconds = 12;

    public static void Show(ChatEntry entry)
    {
        current = entry;
        shownAt = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
    }

    /// <summary>
    /// Wrap a line drawn in the top left so it stays clear of the game's clock and date box in the top right (about
    /// 300 pixels wide). Translations run longer than English.
    /// </summary>
    public static string WrapClearOfClock(string text) =>
        Game1.parseText(text, Game1.smallFont, Math.Max(300, Game1.uiViewport.Width - 32 - 360));

    public static void Draw(SpriteBatch b)
    {
        if (current == null || Game1.activeClickableMenu is ChatOverlayMenu)
            return;
        double age = Game1.currentGameTime.TotalGameTime.TotalSeconds - shownAt;
        if (age > Seconds)
        {
            current = null;
            return;
        }

        int width = Math.Min(560, Game1.uiViewport.Width / 2);
        string label = current.Sender switch
        {
            ChatSender.Warning => I18n.Get("chat.warning"),
            ChatSender.Tip => I18n.Get("chat.tip"),
            _ => I18n.Get("chat.pilot"),
        };
        string text = Game1.parseText(label + current.Text, Game1.smallFont, width - 48);
        string[] lines = text.Split('\n');
        if (lines.Length > 7)
            text = string.Join('\n', lines[..7]) + " ...";
        Vector2 size = Game1.smallFont.MeasureString(text);
        int height = (int)size.Y + 48;
        // Upper left, clear of the game's own HUD messages (bottom left) and the toolbar (top centre).
        int x = 16;
        int y = Math.Max(16, Game1.uiViewport.Height / 4 - height / 2);
        float alpha = age > Seconds - 1 ? (float)(Seconds - age) : 1f;

        Color tint = current.Sender == ChatSender.Warning ? new Color(255, 200, 200) : Color.White;
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), x, y, width, height, tint * alpha, drawShadow: true);
        Color color = current.Sender == ChatSender.Warning ? new Color(150, 20, 20) : Game1.textColor;
        b.DrawString(Game1.smallFont, text, new Vector2(x + 24, y + 24), color * alpha);
    }
}
