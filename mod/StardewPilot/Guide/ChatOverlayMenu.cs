using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace StardewPilot.Guide;

/// <summary>The private copilot chat window. Other players never see it.</summary>
internal sealed class ChatOverlayMenu : IClickableMenu
{
    private readonly Action<string> send;
    private readonly TextBox input;
    private int scrollLines;

    public ChatOverlayMenu(Action<string> send)
        : base(0, 0, 0, 0, showUpperRightCloseButton: true)
    {
        this.send = send;
        this.input = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor)
        {
            textLimit = 300,
            limitWidth = false,
        };
        this.input.OnEnterPressed += _ => this.Submit();
        this.Layout();
        this.input.Selected = true;
    }

    private void Layout()
    {
        var vp = Game1.uiViewport;
        this.width = Math.Min(1000, vp.Width - 64);
        this.height = Math.Min(640, vp.Height - 64);
        this.xPositionOnScreen = (vp.Width - this.width) / 2;
        this.yPositionOnScreen = (vp.Height - this.height) / 2;
        this.input.X = this.xPositionOnScreen + 32;
        this.input.Y = this.yPositionOnScreen + this.height - 80;
        this.input.Width = this.width - 64;
        this.initializeUpperRightCloseButton();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds) => this.Layout();

    private void Submit()
    {
        string text = this.input.Text.Trim();
        if (text.Length == 0)
            return;
        this.input.Text = "";
        this.scrollLines = 0;
        this.send(text);
    }

    public override void receiveKeyPress(Keys key)
    {
        // Typing goes to the text box; only Escape closes. This stops letters like E from closing the window.
        if (key == Keys.Escape)
            this.exitThisMenu();
    }

    public override void receiveScrollWheelAction(int direction)
    {
        this.scrollLines = Math.Max(0, this.scrollLines + (direction > 0 ? 3 : -3));
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);
        this.input.Selected = true;
    }

    protected override void cleanupBeforeExit()
    {
        this.input.Selected = false;
        base.cleanupBeforeExit();
    }

    public override void update(GameTime time)
    {
        base.update(time);
        if (!this.input.Selected)
            this.input.Selected = true;
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.45f);
        drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

        SpriteText.drawStringHorizontallyCenteredAt(b, "StardewPilot", this.xPositionOnScreen + this.width / 2, this.yPositionOnScreen + 24);

        // Conversation, newest at the bottom.
        int left = this.xPositionOnScreen + 36;
        int textWidth = this.width - 72;
        int top = this.yPositionOnScreen + 80;
        int bottom = this.input.Y - 44;
        int lineHeight = (int)Game1.smallFont.MeasureString("Ag").Y;

        var lines = new List<(string text, Color color)>();
        foreach (ChatEntry entry in ChatLog.All)
        {
            (string label, Color color) = entry.Sender switch
            {
                ChatSender.Player => (I18n.Get("chat.you"), new Color(40, 70, 140)),
                ChatSender.Agent => (I18n.Get("chat.pilot"), Game1.textColor),
                ChatSender.Tip => (I18n.Get("chat.tip"), new Color(30, 110, 40)),
                ChatSender.Warning => (I18n.Get("chat.warning"), new Color(170, 30, 30)),
                _ => ("", Color.DimGray),
            };
            foreach (string line in Game1.parseText(label + entry.Text, Game1.smallFont, textWidth).Split('\n'))
                lines.Add((line, color));
            lines.Add(("", color));
        }

        int visible = Math.Max(1, (bottom - top) / lineHeight);
        int maxScroll = Math.Max(0, lines.Count - visible);
        this.scrollLines = Math.Min(this.scrollLines, maxScroll);
        int start = Math.Max(0, lines.Count - visible - this.scrollLines);
        int y = top;
        for (int i = start; i < Math.Min(lines.Count, start + visible); i++, y += lineHeight)
            b.DrawString(Game1.smallFont, lines[i].text, new Vector2(left, y), lines[i].color);

        if (ChatLog.All.Count == 0)
            b.DrawString(Game1.smallFont, Game1.parseText(I18n.Get("chat.empty"), Game1.smallFont, textWidth), new Vector2(left, top), Color.DimGray);

        // Status line above the input.
        string status = !AgentState.Connected
            ? StardewPilot.Bridge.WrapperStatus.NotConnectedHint()
            : AgentState.Busy
                ? I18n.Get("chat.thinking") + new string('.', (int)(Game1.currentGameTime.TotalGameTime.TotalSeconds * 2 % 4)) + (AgentState.Activity.Length > 0 ? $"  ({AgentState.Activity})" : "")
                : I18n.Get("chat.help");
        status = Game1.parseText(status, Game1.smallFont, textWidth).Split('\n')[0]; // one line; the chat log has the full text
        b.DrawString(Game1.smallFont, status, new Vector2(left, this.input.Y - 36), Color.DimGray);

        this.input.Draw(b);
        base.draw(b);
        this.drawMouse(b);
    }
}
