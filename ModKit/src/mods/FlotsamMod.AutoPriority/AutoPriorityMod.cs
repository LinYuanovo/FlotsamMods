using System;
using System.Text;
using FlotsamModKit.Abstractions;
using FlotsamModKit.Game;
using UnityEngine;

namespace FlotsamMods.AutoPriority
{
    /// <summary>
    /// Two event-driven colony automations, HUD toggles only (no panel):
    ///
    /// 1. 自动任务优先级 (Ctrl+3) — re-ranks every work assignment across the colony by the
    ///    relevant drifter skill: best third → Highest, middle → Default, rest → Lowest, so the
    ///    right drifter wins the right job (algorithm in GameAssignments.RunAutoPriority).
    /// 2. 自动加点 (Ctrl+4) — spends each drifter's banked attribute points through the game's
    ///    own TryLevelAttribute: ❤ affinity attributes first, then the already-highest attribute
    ///    (specialize). Every auto-spend that moved points drags one priority re-rank along.
    ///
    /// No polling. Passes run on the game's own events — AgentLevelGained (points to spend),
    /// AgentAttributeLeveled (a skill changed, manually or by us), AgentAddedToPlayerCommunity /
    /// AgentRemovedFromPlayerCommunity / AgentDeath (roster changed) — coalesced by a short
    /// debounce into at most one deferred run, executed from OnTick (never inside an event
    /// callback, so we never re-enter game logic mid-dispatch). Switching a toggle on runs its
    /// pass immediately with a toast; event runs stay silent (native priority boxes / point
    /// counters are the visible feedback) and log one line only when something moved.
    ///
    /// While a toggle is on the mod owns that half: manual priority changes are re-ranked on the
    /// next trigger, banked points are spent on the next level-up. Toggle off to take manual
    /// control back; nothing is reverted. All game access lives in the Game layer.
    /// </summary>
    public sealed class AutoPriorityMod : FlotsamModBase
    {
        private IKeybind _prioKey;
        private IKeybind _spendKey;
        private IHudButton _prioButton;
        private IHudButton _spendButton;

        private bool _autoPriority;
        private bool _autoSpend;
        private float _debounce = 0.5f;
        private float _highBelow = GameAssignments.DefaultHighBelow;
        private float _midBelow = GameAssignments.DefaultMidBelow;
        private bool _verbose;

        private bool _running;
        private bool _iconsSet;
        private bool _pendSpend, _pendRerank;
        private float _pendAt = -1f;

        public override void OnLoad(IModContext context)
        {
            base.OnLoad(context);
            _prioKey = Keybinds.Register("autopriority.toggle", KeyCode.Alpha3, "开关自动任务优先级(Ctrl+3)");
            _spendKey = Keybinds.Register("autospend.toggle", KeyCode.Alpha4, "开关自动加点(Ctrl+4)");
            _autoPriority = Config.Get("active", true);
            _autoSpend = Config.Get("autoSpend", true);
            _debounce = Mathf.Clamp(Config.Get("debounceSec", 0.5f), 0.1f, 10f);
            _highBelow = Mathf.Clamp01(Config.Get("tierHighBelow", GameAssignments.DefaultHighBelow));
            _midBelow = Mathf.Clamp01(Config.Get("tierMidBelow", GameAssignments.DefaultMidBelow));
            if (_midBelow < _highBelow) _midBelow = _highBelow;
            _verbose = Config.Get("verbose", false);
            if (Config.Get("schema", 0) < 1) { Config.Set("schema", 1); Config.Save(); }

            Log.Info($"loaded — autoPriority={_autoPriority}, autoSpend={_autoSpend}, " +
                     $"debounce={_debounce:0.##}s, tiers high<={_highBelow:0.##} mid<={_midBelow:0.##}");
        }

        public override void OnEnable()
        {
            _prioButton = Ui.AddHudButton("autopriority.toggle", PrioLabel, TogglePriority,
                                          HudAnchor.RightMiddle, Config, "button");
            _prioButton.Visible = true;

            _spendButton = Ui.AddHudButton("autospend.toggle", SpendLabel, ToggleSpend,
                                           HudAnchor.RightMiddle, Config, "spendbutton");
            _spendButton.Visible = true;
            // Icons are set later in OnTick: harvested sprites only exist inside a save
            // (same deferred pattern as BatchManager — OnEnable runs on the main menu,
            // where NativeSkin.Find would return null forever).

            // level-up only matters for spending (banked points do not change task ranks);
            // a skill actually levelling (ours or the player's) re-ranks; roster changes re-rank
            // and screen a newcomer's banked points.
            Events.On("AgentLevelGained", _ => Queue(_autoSpend, false));
            Events.On("AgentAttributeLeveled", _ => Queue(false, _autoPriority));
            Events.On("AgentAddedToPlayerCommunity", _ => Queue(_autoSpend, _autoPriority));
            Events.On("AgentRemovedFromPlayerCommunity", _ => Queue(false, _autoPriority));
            Events.On("AgentDeath", _ => Queue(false, _autoPriority));

            SyncButtons();
            Log.Info("auto-priority ready (event-driven: level-up / attribute / roster)");
        }

        public override void OnDisable()
        {
            try { _prioButton?.Destroy(); } catch { }
            try { _spendButton?.Destroy(); } catch { }
            _prioButton = null;
            _spendButton = null;
            _iconsSet = false;
            ClearPending();
            Log.Info("auto-priority removed");
        }

        public override void OnGameStart()
        {
            // catch up on points banked / ranks drifted while the mod was off; deferred so the
            // world finishes spawning first (the pending run no-ops until IsPlaying anyway).
            Queue(_autoSpend, _autoPriority, 1.5f);
            Ui.Toast($"自动任务优先级:{State(_autoPriority)}，自动加点:{State(_autoSpend)}" +
                     "（Ctrl+3 / Ctrl+4 或 HUD 按钮切换；升级·加点·成员变动时自动重排）",
                     ToastKind.Success);
        }

        public override void OnGameEnd()
        {
            _running = false;
            _iconsSet = false;   // harvested sprites die with the scene; re-apply icons next save
            ClearPending();
        }

        public override void OnTick()
        {
            if (!_iconsSet && GameApi.IsPlaying && NativeSkin.Available)
            {
                _iconsSet = true;
                try
                {
                    _prioButton?.SetIcon(NativeSkin.Find("HUD_DrifterExpertise", "HUD_DrifterDuties",
                                                         "DrifterPanel_Expertise", "InfoPanel_Priority_VeryHigh"));
                    _spendButton?.SetIcon(NativeSkin.Find("Affinity_Heart", "InfoPanel_Plus"));
                }
                catch { }
            }

            if (_prioKey != null && _prioKey.IsDown && GameKeys.GetCtrlHeld()) TogglePriority();
            if (_spendKey != null && _spendKey.IsDown && GameKeys.GetCtrlHeld()) ToggleSpend();

            if (_running || _pendAt < 0f) return;
            if (Time.realtimeSinceStartup < _pendAt) return;
            bool spend = _pendSpend, rerank = _pendRerank;
            ClearPending();
            Run(spend, rerank, manual: false);
        }

        // ------------------------------------------------------------ state / labels

        private static string State(bool on) => on ? "开" : "关";
        private string PrioLabel => _autoPriority ? "优先级:自动" : "优先级:手动";
        private string SpendLabel => _autoSpend ? "加点:自动" : "加点:手动";

        private void SyncButtons()
        {
            try { _prioButton?.SetLabel(PrioLabel); } catch { }
            try { _spendButton?.SetLabel(SpendLabel); } catch { }
        }

        private void ClearPending()
        {
            _pendSpend = false;
            _pendRerank = false;
            _pendAt = -1f;
        }

        /// <summary>Coalesces event bursts into one deferred run; the latest trigger resets the
        /// delay so a batch of level-ups/joins produces a single pass right after the last one.</summary>
        private void Queue(bool spend, bool rerank, float delay = -1f)
        {
            if (!spend && !rerank) return;
            _pendSpend |= spend;
            _pendRerank |= rerank;
            _pendAt = Time.realtimeSinceStartup + (delay >= 0f ? delay : _debounce);
        }

        // ------------------------------------------------------------ toggles

        private void TogglePriority()
        {
            _autoPriority = !_autoPriority;
            Config.Set("active", _autoPriority);
            Config.Save();
            SyncButtons();

            if (!_autoPriority)
            {
                Ui.Toast("自动任务优先级已关闭（保留当前优先级，可手动调整）", ToastKind.Info);
                Log.Info("auto-priority off");
                return;
            }
            if (GameApi.IsPlaying) Run(false, true, manual: true);
            else
            {
                Queue(false, true, 1f);
                Ui.Toast("自动任务优先级已开启（进入存档后生效）", ToastKind.Info);
            }
        }

        private void ToggleSpend()
        {
            _autoSpend = !_autoSpend;
            Config.Set("autoSpend", _autoSpend);
            Config.Save();
            SyncButtons();

            if (!_autoSpend)
            {
                Ui.Toast("自动加点已关闭（技能点保留，可手动分配）", ToastKind.Info);
                Log.Info("auto-spend off");
                return;
            }
            if (GameApi.IsPlaying) Run(true, _autoPriority, manual: true);
            else
            {
                Queue(true, _autoPriority, 1f);
                Ui.Toast("自动加点已开启（进入存档后生效）", ToastKind.Info);
            }
        }

        // ------------------------------------------------------------ the pass

        /// <summary>One pass: spend first (skills change), then re-rank so the new skills show.</summary>
        private void Run(bool spend, bool rerank, bool manual)
        {
            if (_running) { Queue(spend, rerank); return; }
            if (!GameApi.IsPlaying) { Queue(spend, rerank, 1f); return; }

            _running = true;
            try
            {
                AutoSpendResult spent = null;
                if (spend && _autoSpend)
                    spent = GameAssignments.RunAutoSpend(_verbose, m => Log.Info(m));

                // an auto-spend that moved points always drags one re-rank along
                if (spent != null && spent.HasChanges) rerank = true;

                AutoPriorityResult ranked = null;
                if (rerank && _autoPriority)
                    ranked = GameAssignments.RunAutoPriority(_highBelow, _midBelow, _verbose, m => Log.Info(m));

                Report(spent, ranked, manual);
            }
            catch (Exception e)
            {
                Log.Error("auto pass failed", e);
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>Manual runs toast; event runs never do (anti-spam). One log line when
        /// something moved, or on any run under verbose.</summary>
        private void Report(AutoSpendResult spent, AutoPriorityResult ranked, bool manual)
        {
            bool didSomething = (spent != null && !spent.Blocked && spent.HasChanges)
                             || (ranked != null && !ranked.Blocked && ranked.HasChanges);

            if (manual)
            {
                var parts = new StringBuilder();
                if (spent != null) parts.Append(spent.SummaryText());
                if (ranked != null)
                {
                    if (parts.Length > 0) parts.Append('；');
                    parts.Append(ranked.SummaryText());
                }
                Ui.Toast(parts.Length > 0 ? parts.ToString() : "已就绪",
                         didSomething ? ToastKind.Success : ToastKind.Info);
            }

            if (didSomething || _verbose)
            {
                var line = new StringBuilder("run").Append(manual ? "(manual): " : "(event): ");
                if (spent != null) line.Append("spend[").Append(spent.LogLine()).Append(']');
                if (ranked != null)
                {
                    if (spent != null) line.Append(' ');
                    line.Append("rank[").Append(ranked.LogLine()).Append(']');
                }
                Log.Info(line.ToString());
            }
        }
    }
}
