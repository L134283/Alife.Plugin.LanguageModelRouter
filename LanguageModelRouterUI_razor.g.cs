using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Rendering;
using AntDesign;

namespace Alife.Plugin.LanguageModelRouter;

public partial class LanguageModelRouterUI : ModuleUIBase<LanguageModelRouter, LanguageModelRouterConfig>, IDisposable
{
    string?[] _detectResults = Array.Empty<string?>();
    List<string>?[] _detectedModels = Array.Empty<List<string>?>();
    int _seq;
    int? _dragSource;

    /// <summary>每组渠道卡片的折叠状态（受控，避免任何 StateHasChanged 重渲染后原生 &lt;details&gt; 被意外收起）。
    /// 键为 GroupChannel 引用，拖动排序后展开状态跟随原组内容而不是位置。</summary>
    readonly Dictionary<GroupChannel, bool> _groupOpenStates = new();

    /// <summary>通用折叠区状态（键为区块标识，如 "help"、"disaster"、"group:2:adv"），
    /// 与 <see cref="_groupOpenStates"/> 同理：受控保存，重渲染后不丢失。</summary>
    readonly Dictionary<string, bool> _foldStates = new();

    // 订阅标记，Dispose 时退订静态事件，避免累积订阅（内存泄漏 + 重复刷新）
    bool _subscribed;

    const string Css = @"
.ls-container {
  --ls-bg: #080706;
  --ls-bg2: #120f0a;
  --ls-bg3: #1c1710;
  --ls-gold: #e8c65a;
  --ls-gold-bright: #fff0b0;
  --ls-gold-dim: #c9a43a;
  --ls-amber: #dcb44a;
  --ls-border: #5a4a28;
  --ls-border-soft: #3d3420;
  --ls-text: #f7efd8;
  --ls-text-dim: #e0d2a4;
  --ls-text-mute: #c4b07a;
  --ls-danger: #ef7a68;
  --ls-ok: #a8d47a;
  --ls-crimson: #8b2e2e;
  position: relative;
  max-width: 740px;
  padding: 32px 28px 36px;
  border-radius: 18px;
  isolation: isolate;
  background:
    radial-gradient(ellipse 90% 55% at 50% -15%, rgba(255,220,120,0.22), transparent 55%),
    radial-gradient(ellipse 45% 35% at 100% 0%, rgba(180,40,40,0.08), transparent 50%),
    radial-gradient(ellipse 40% 30% at 0% 100%, rgba(212,175,55,0.1), transparent 50%),
    radial-gradient(ellipse 60% 40% at 80% 90%, rgba(120,60,20,0.12), transparent 55%),
    linear-gradient(165deg, #16120c 0%, #0a0907 45%, #0e0b08 100%);
  border: 1px solid transparent;
  background-clip: padding-box;
  box-shadow:
    0 0 0 1px rgba(232,198,90,0.35),
    0 0 0 2px rgba(20,16,8,0.9),
    0 0 0 3px rgba(232,198,90,0.12),
    0 20px 60px rgba(0,0,0,0.55),
    0 0 80px rgba(232,198,90,0.1),
    inset 0 1px 0 rgba(255,240,176,0.08);
  color: var(--ls-text);
  font-family: 'Segoe UI', 'Microsoft YaHei', 'PingFang SC', system-ui, sans-serif;
  overflow: hidden;
  animation: ls-rise 0.7s cubic-bezier(.2,.8,.2,1);
}
.ls-container::before {
  content: '';
  position: absolute;
  inset: -50%;
  background:
    conic-gradient(from 0deg, transparent 0deg, rgba(255,230,140,0.07) 40deg, transparent 80deg, transparent 180deg, rgba(255,200,80,0.05) 220deg, transparent 280deg);
  animation: ls-aura-spin 18s linear infinite;
  pointer-events: none;
  z-index: 0;
}
.ls-frame {
  position: absolute;
  inset: 7px;
  border: 1px solid rgba(232,198,90,0.18);
  border-radius: 13px;
  pointer-events: none;
  z-index: 1;
  box-shadow: inset 0 0 30px rgba(232,198,90,0.04);
}
.ls-frame::before {
  content: '';
  position: absolute;
  inset: 4px;
  border: 1px dashed rgba(232,198,90,0.1);
  border-radius: 10px;
  animation: ls-dash 20s linear infinite;
}
.ls-border-glow {
  position: absolute;
  inset: 0;
  border-radius: 18px;
  pointer-events: none;
  z-index: 1;
  background: linear-gradient(120deg, transparent 20%, rgba(255,240,176,0.35) 45%, rgba(232,198,90,0.15) 55%, transparent 80%);
  background-size: 250% 250%;
  animation: ls-border-flow 5s linear infinite;
  opacity: 0.55;
  mix-blend-mode: screen;
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask-composite: exclude;
  padding: 1.5px;
}
.ls-particles {
  position: absolute;
  inset: 0;
  pointer-events: none;
  z-index: 1;
  overflow: hidden;
}
.ls-particle {
  position: absolute;
  bottom: -5%;
  width: 3px; height: 3px;
  border-radius: 50%;
  background: radial-gradient(circle at 35% 35%, #fffef5, var(--ls-gold-bright) 45%, var(--ls-gold) 100%);
  box-shadow:
    0 0 6px rgba(255,250,220,1),
    0 0 14px rgba(255,240,176,0.95),
    0 0 28px rgba(232,198,90,0.65),
    0 0 42px rgba(232,198,90,0.3);
  opacity: 0;
  animation: ls-float-up 7s ease-in-out infinite, ls-sparkle 2.2s ease-in-out infinite;
  will-change: transform, opacity;
}
.ls-particle-sm {
  width: 2px; height: 2px;
  box-shadow:
    0 0 4px rgba(255,250,220,0.95),
    0 0 10px rgba(255,240,176,0.8),
    0 0 18px rgba(232,198,90,0.45);
}
.ls-particle-md {
  width: 3px; height: 3px;
}
.ls-particle-lg {
  width: 5px; height: 5px;
  box-shadow:
    0 0 8px rgba(255,255,240,1),
    0 0 18px rgba(255,240,176,1),
    0 0 36px rgba(232,198,90,0.75),
    0 0 56px rgba(232,198,90,0.35);
}
.ls-particle-xl {
  width: 7px; height: 7px;
  box-shadow:
    0 0 10px rgba(255,255,245,1),
    0 0 22px rgba(255,240,176,1),
    0 0 44px rgba(232,198,90,0.85),
    0 0 70px rgba(255,220,120,0.4);
}
.ls-rune {
  position: absolute;
  pointer-events: none;
  z-index: 1;
  color: var(--ls-gold);
  font-size: 12px;
  opacity: 0.18;
  text-shadow: 0 0 12px rgba(232,198,90,0.4);
  animation: ls-rune-drift 12s ease-in-out infinite;
}
.ls-rune-1 { top: 18%; left: 4%;  animation-delay: 0s; }
.ls-rune-2 { top: 42%; right: 3%; animation-delay: 2s; font-size: 14px; }
.ls-rune-3 { bottom: 22%; left: 5%; animation-delay: 4s; }
.ls-rune-4 { top: 70%; right: 6%; animation-delay: 1s; font-size: 11px; }
.ls-corner {
  position: absolute;
  width: 28px; height: 28px;
  pointer-events: none;
  z-index: 2;
}
.ls-corner::before, .ls-corner::after {
  content: '';
  position: absolute;
  background: linear-gradient(90deg, var(--ls-gold-bright), var(--ls-gold));
  box-shadow: 0 0 8px rgba(255,240,176,0.5);
}
.ls-corner-tl { top: 4px; left: 4px; }
.ls-corner-tl::before { top: 0; left: 0; width: 22px; height: 2px; }
.ls-corner-tl::after  { top: 0; left: 0; width: 2px; height: 22px; }
.ls-corner-tr { top: 4px; right: 4px; }
.ls-corner-tr::before { top: 0; right: 0; width: 22px; height: 2px; }
.ls-corner-tr::after  { top: 0; right: 0; width: 2px; height: 22px; }
.ls-corner-bl { bottom: 4px; left: 4px; }
.ls-corner-bl::before { bottom: 0; left: 0; width: 22px; height: 2px; }
.ls-corner-bl::after  { bottom: 0; left: 0; width: 2px; height: 22px; }
.ls-corner-br { bottom: 4px; right: 4px; }
.ls-corner-br::before { bottom: 0; right: 0; width: 22px; height: 2px; }
.ls-corner-br::after  { bottom: 0; right: 0; width: 2px; height: 22px; }
.ls-corner-gem {
  position: absolute;
  width: 6px; height: 6px;
  border-radius: 50%;
  background: radial-gradient(circle at 35% 35%, #fff8d0, var(--ls-gold));
  box-shadow: 0 0 10px rgba(255,240,176,0.8);
  animation: ls-pulse 2.5s ease-in-out infinite;
}
.ls-corner-tl .ls-corner-gem { top: -1px; left: -1px; }
.ls-corner-tr .ls-corner-gem { top: -1px; right: -1px; }
.ls-corner-bl .ls-corner-gem { bottom: -1px; left: -1px; }
.ls-corner-br .ls-corner-gem { bottom: -1px; right: -1px; }
.ls-inner { position: relative; z-index: 3; }

@keyframes ls-rise {
  from { opacity: 0; transform: translateY(14px) scale(0.985); filter: blur(4px); }
  to   { opacity: 1; transform: none; filter: none; }
}
@keyframes ls-pulse {
  0%, 100% { opacity: 0.45; filter: brightness(0.9); }
  50% { opacity: 1; filter: brightness(1.3); }
}
@keyframes ls-shimmer {
  0% { background-position: -200% center; }
  100% { background-position: 200% center; }
}
@keyframes ls-breathe {
  0%, 100% { box-shadow: 0 0 0 0 rgba(232,198,90,0), 0 0 10px rgba(232,198,90,0.2); }
  50% { box-shadow: 0 0 0 4px rgba(232,198,90,0.1), 0 0 24px rgba(255,240,176,0.45); }
}
@keyframes ls-glow-line {
  0%, 100% { opacity: 0.35; }
  50% { opacity: 1; }
}
@keyframes ls-seal-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
@keyframes ls-seal-spin-rev {
  from { transform: rotate(360deg); }
  to { transform: rotate(0deg); }
}
@keyframes ls-aura-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
@keyframes ls-border-flow {
  0% { background-position: 0% 50%; }
  100% { background-position: 200% 50%; }
}
@keyframes ls-float-up {
  0%   { bottom: -5%; opacity: 0; transform: translateX(0) scale(0.6); }
  15%  { opacity: 0.85; }
  50%  { opacity: 0.5; transform: translateX(8px) scale(1); }
  100% { bottom: 105%; opacity: 0; transform: translateX(-6px) scale(0.4); }
}
@keyframes ls-rune-drift {
  0%, 100% { transform: translateY(0) rotate(0deg); opacity: 0.12; }
  50% { transform: translateY(-10px) rotate(8deg); opacity: 0.28; }
}
@keyframes ls-dash {
  to { stroke-dashoffset: -40; }
}
@keyframes ls-ring-pulse {
  0%, 100% { transform: scale(1); opacity: 0.5; }
  50% { transform: scale(1.08); opacity: 0.9; }
}
@keyframes ls-title-glow {
  0%, 100% { filter: drop-shadow(0 0 8px rgba(232,198,90,0.3)); }
  50% { filter: drop-shadow(0 0 18px rgba(255,240,176,0.65)); }
}
@keyframes ls-chip-shine {
  0% { left: -60%; }
  100% { left: 120%; }
}
@keyframes ls-fade-in {
  from { opacity: 0; transform: translateY(6px); }
  to { opacity: 1; transform: none; }
}

/* ===== Title / Crest ===== */
.ls-title-wrap {
  text-align: center;
  margin-bottom: 20px;
  padding: 8px 0 18px;
  position: relative;
}
.ls-crest {
  position: relative;
  width: 72px; height: 72px;
  margin: 0 auto 12px;
}
.ls-crest-ring {
  position: absolute;
  inset: 0;
  border-radius: 50%;
  border: 1.5px solid rgba(232,198,90,0.45);
  box-shadow: 0 0 16px rgba(232,198,90,0.25), inset 0 0 12px rgba(232,198,90,0.1);
  animation: ls-seal-spin 14s linear infinite;
}
.ls-crest-ring::before {
  content: '';
  position: absolute;
  top: -3px; left: 50%;
  width: 6px; height: 6px;
  margin-left: -3px;
  border-radius: 50%;
  background: var(--ls-gold-bright);
  box-shadow: 0 0 10px rgba(255,240,176,0.9);
}
.ls-crest-ring-inner {
  position: absolute;
  inset: 8px;
  border-radius: 50%;
  border: 1px dashed rgba(232,198,90,0.35);
  animation: ls-seal-spin-rev 10s linear infinite;
}
.ls-crest-core {
  position: absolute;
  inset: 16px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 18px;
  color: #1a1408;
  font-weight: 800;
  background:
    radial-gradient(circle at 35% 30%, #fff6c8, var(--ls-gold) 45%, var(--ls-amber) 100%);
  box-shadow:
    0 0 20px rgba(255,240,176,0.55),
    0 0 40px rgba(232,198,90,0.25),
    inset 0 1px 2px rgba(255,255,255,0.5);
  animation: ls-breathe 3s ease-in-out infinite;
  letter-spacing: 0;
}
.ls-crest-halo {
  position: absolute;
  inset: -6px;
  border-radius: 50%;
  border: 1px solid rgba(255,240,176,0.15);
  animation: ls-ring-pulse 3.5s ease-in-out infinite;
}
.ls-title-ornament {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 12px;
  margin-bottom: 10px;
  color: var(--ls-gold-bright);
  font-size: 11px;
  letter-spacing: 5px;
  text-shadow: 0 0 12px rgba(255,240,176,0.35);
}
.ls-title-ornament::before,
.ls-title-ornament::after {
  content: '';
  flex: 1;
  max-width: 90px;
  height: 1px;
  background: linear-gradient(90deg, transparent, var(--ls-gold-bright), transparent);
  box-shadow: 0 0 8px rgba(255,240,176,0.4);
  animation: ls-glow-line 2.5s ease-in-out infinite;
}
.ls-title {
  font-size: 19px;
  font-weight: 800;
  letter-spacing: 1.5px;
  background: linear-gradient(100deg,
    #a88420 0%,
    var(--ls-gold-bright) 25%,
    #fff 40%,
    var(--ls-gold-bright) 55%,
    var(--ls-gold) 70%,
    var(--ls-gold-dim) 100%);
  background-size: 220% auto;
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
  animation: ls-shimmer 4s linear infinite, ls-title-glow 3s ease-in-out infinite;
}
.ls-title-sub {
  margin-top: 8px;
  font-size: 11px;
  color: var(--ls-text-dim);
  letter-spacing: 4px;
  text-shadow: 0 0 10px rgba(232,198,90,0.2);
}
.ls-title-bar {
  width: 120px;
  height: 2px;
  margin: 12px auto 0;
  background: linear-gradient(90deg, transparent, var(--ls-gold-bright), var(--ls-gold), transparent);
  box-shadow: 0 0 12px rgba(255,240,176,0.5);
  border-radius: 2px;
  animation: ls-glow-line 2s ease-in-out infinite;
}

/* ===== Alert / Oracle ===== */
.ls-alert {
  position: relative;
  margin-bottom: 22px;
  padding: 18px 16px 16px;
  border-radius: 14px;
  overflow: hidden;
  border: 1px solid rgba(232,198,90,0.4);
  background:
    radial-gradient(ellipse 80% 90% at 0% 0%, rgba(255,230,140,0.14), transparent 55%),
    radial-gradient(ellipse 50% 60% at 100% 100%, rgba(180,40,40,0.08), transparent 50%),
    linear-gradient(155deg, rgba(36,28,14,0.96) 0%, rgba(10,8,6,0.98) 100%);
  box-shadow:
    0 0 0 1px rgba(232,198,90,0.08) inset,
    0 10px 36px rgba(0,0,0,0.4),
    0 0 50px rgba(232,198,90,0.1);
}
.ls-alert::before {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(105deg, transparent 35%, rgba(255,240,176,0.1) 50%, transparent 65%);
  background-size: 220% 100%;
  animation: ls-shimmer 5s linear infinite;
  pointer-events: none;
}
.ls-alert::after {
  content: '';
  position: absolute;
  left: 0; top: 0; bottom: 0;
  width: 3px;
  background: linear-gradient(180deg, transparent, var(--ls-gold-bright), var(--ls-gold), transparent);
  box-shadow: 0 0 16px rgba(255,240,176,0.7);
}
.ls-alert-header {
  position: relative;
  z-index: 1;
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 14px;
  padding-bottom: 10px;
  border-bottom: 1px solid rgba(232,198,90,0.2);
}
.ls-alert-header-icon {
  width: 26px; height: 26px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 12px;
  color: #1a1408;
  background: linear-gradient(135deg, #fff6c8, var(--ls-gold), var(--ls-amber));
  box-shadow: 0 0 18px rgba(255,240,176,0.55);
  animation: ls-breathe 2.8s ease-in-out infinite;
  flex-shrink: 0;
}
.ls-alert-header-title {
  font-size: 13px;
  font-weight: 800;
  letter-spacing: 3px;
  color: var(--ls-gold-bright);
  text-shadow: 0 0 14px rgba(255,240,176,0.4);
}
.ls-alert-header-line {
  flex: 1;
  height: 1px;
  background: linear-gradient(90deg, rgba(232,198,90,0.5), transparent);
  box-shadow: 0 0 6px rgba(232,198,90,0.3);
}
.ls-alert-body {
  position: relative;
  z-index: 1;
  display: flex;
  flex-direction: column;
  gap: 9px;
}
.ls-alert-line {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  padding: 10px 12px;
  border-radius: 10px;
  background: linear-gradient(135deg, rgba(232,198,90,0.06), rgba(0,0,0,0.15));
  border: 1px solid rgba(232,198,90,0.14);
  transition: all 0.3s cubic-bezier(.2,.8,.2,1);
  animation: ls-fade-in 0.5s ease both;
}
.ls-alert-line:nth-child(1) { animation-delay: 0.06s; }
.ls-alert-line:nth-child(2) { animation-delay: 0.14s; }
.ls-alert-line:nth-child(3) { animation-delay: 0.22s; }
.ls-alert-line:nth-child(4) { animation-delay: 0.3s; }
.ls-alert-line:hover {
  border-color: rgba(255,240,176,0.45);
  background: linear-gradient(135deg, rgba(232,198,90,0.14), rgba(40,30,10,0.4));
  transform: translateX(5px) scale(1.01);
  box-shadow: 0 0 22px rgba(232,198,90,0.15), inset 0 0 20px rgba(232,198,90,0.04);
}
.ls-alert-mark {
  flex-shrink: 0;
  width: 22px; height: 22px;
  margin-top: 1px;
  border-radius: 50%;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  color: var(--ls-gold-bright);
  background: radial-gradient(circle at 40% 35%, rgba(255,240,176,0.3), rgba(20,16,8,0.95));
  border: 1px solid rgba(232,198,90,0.55);
  box-shadow: 0 0 12px rgba(232,198,90,0.3);
  text-shadow: 0 0 8px rgba(255,240,176,0.6);
}
.ls-alert-line-warn {
  background: linear-gradient(135deg, rgba(232,198,90,0.12), rgba(80,40,10,0.25));
  border-color: rgba(255,240,176,0.35);
  box-shadow: 0 0 24px rgba(232,198,90,0.1) inset;
}
.ls-alert-line-warn .ls-alert-mark {
  color: #1a1408;
  background: linear-gradient(135deg, #fff6c8, var(--ls-gold));
  border-color: var(--ls-gold-bright);
  box-shadow: 0 0 18px rgba(255,240,176,0.55);
  animation: ls-pulse 2.2s ease-in-out infinite;
}
.ls-alert-text {
  flex: 1;
  font-size: 12.5px;
  line-height: 1.7;
  color: var(--ls-text);
}

/* ===== Section ===== */
.ls-section {
  margin-top: 24px;
  position: relative;
  padding: 14px 14px 12px;
  border-radius: 12px;
  background: linear-gradient(160deg, rgba(232,198,90,0.05) 0%, transparent 50%);
  border: 1px solid rgba(232,198,90,0.1);
  box-shadow: inset 0 1px 0 rgba(255,240,176,0.04);
}
.ls-section-title {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 13.5px;
  font-weight: 800;
  color: var(--ls-gold-bright);
  margin: 0 0 14px;
  padding-bottom: 10px;
  border-bottom: 1px solid rgba(232,198,90,0.18);
  letter-spacing: 1px;
  text-shadow: 0 0 12px rgba(255,240,176,0.25);
}
.ls-section-title::before {
  content: '❖';
  font-size: 12px;
  color: var(--ls-gold);
  text-shadow: 0 0 10px rgba(232,198,90,0.6);
  animation: ls-pulse 3.5s ease-in-out infinite;
}
.ls-section-title::after {
  content: '';
  flex: 1;
  height: 1px;
  background: linear-gradient(90deg, rgba(232,198,90,0.5), transparent 80%);
  box-shadow: 0 0 6px rgba(232,198,90,0.3);
  margin-left: 4px;
}

/* Labels / hints */
.ls-label {
  font-weight: 700;
  margin-bottom: 5px;
  margin-top: 10px;
  font-size: 12.5px;
  color: var(--ls-text);
  letter-spacing: 0.4px;
  text-shadow: 0 0 8px rgba(232,198,90,0.1);
}
.ls-hint {
  font-size: 11.5px;
  color: var(--ls-text-dim);
  margin: 5px 0 12px 2px;
  line-height: 1.65;
  white-space: pre-line;
  padding-left: 8px;
  border-left: 2px solid rgba(232,198,90,0.2);
}

/* Status / badges */
.ls-status-row {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 12px;
  font-size: 12.5px;
  color: var(--ls-text);
}
.ls-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 12px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.4px;
  border: 1px solid transparent;
}
.ls-badge-on {
  color: var(--ls-gold-bright);
  background: linear-gradient(135deg, rgba(232,198,90,0.18), rgba(232,198,90,0.06));
  border-color: rgba(232,198,90,0.5);
  box-shadow: 0 0 14px rgba(232,198,90,0.2);
}
.ls-badge-off {
  color: var(--ls-text-dim);
  background: rgba(60,50,30,0.5);
  border-color: var(--ls-border);
}
.ls-badge-active {
  color: #1a1408;
  background: linear-gradient(135deg, #fff6c8, var(--ls-gold), var(--ls-amber));
  border-color: var(--ls-gold-bright);
  box-shadow: 0 0 18px rgba(255,240,176,0.45);
  animation: ls-breathe 2.4s ease-in-out infinite;
}

/* Toggle */
.ls-toggle-row {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: 12px;
  padding: 12px 14px;
  border-radius: 10px;
  background: linear-gradient(135deg, rgba(232,198,90,0.07), rgba(0,0,0,0.2));
  border: 1px solid rgba(232,198,90,0.16);
  transition: all 0.28s ease;
  position: relative;
  overflow: hidden;
}
.ls-toggle-row::before {
  content: '';
  position: absolute;
  left: 0; top: 0; bottom: 0;
  width: 2px;
  background: linear-gradient(180deg, transparent, var(--ls-gold), transparent);
  opacity: 0;
  transition: opacity 0.25s;
}
.ls-toggle-row:hover {
  border-color: rgba(255,240,176,0.4);
  background: linear-gradient(135deg, rgba(232,198,90,0.12), rgba(30,24,12,0.5));
  box-shadow: 0 0 20px rgba(232,198,90,0.1);
  transform: translateX(2px);
}
.ls-toggle-row:hover::before { opacity: 1; }
.ls-toggle-row input[type=checkbox] {
  appearance: none;
  -webkit-appearance: none;
  width: 20px; height: 20px;
  margin-top: 1px;
  flex-shrink: 0;
  border: 1.5px solid var(--ls-gold-dim);
  border-radius: 5px;
  background: radial-gradient(circle at 40% 35%, #1a1610, #0a0907);
  cursor: pointer;
  position: relative;
  transition: all 0.25s;
  box-shadow: inset 0 0 6px rgba(0,0,0,0.5);
}
.ls-toggle-row input[type=checkbox]:hover {
  border-color: var(--ls-gold-bright);
  box-shadow: 0 0 10px rgba(232,198,90,0.3);
}
.ls-toggle-row input[type=checkbox]:checked {
  background: linear-gradient(135deg, #fff6c8, var(--ls-gold), var(--ls-amber));
  border-color: var(--ls-gold-bright);
  box-shadow: 0 0 16px rgba(255,240,176,0.55);
}
.ls-toggle-row input[type=checkbox]:checked::after {
  content: '✦';
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  color: #1a1408;
  font-weight: bold;
  text-shadow: 0 0 4px rgba(255,255,255,0.4);
}
.ls-toggle-row label {
  font-size: 13px;
  color: var(--ls-text);
  cursor: pointer;
  line-height: 1.55;
  user-select: none;
  font-weight: 500;
}

/* Buttons */
.ls-btn-row {
  display: flex;
  gap: 10px;
  flex-wrap: wrap;
  margin-bottom: 8px;
}
.ls-btn {
  position: relative;
  padding: 7px 18px;
  border-radius: 9px;
  border: 1px solid rgba(232,198,90,0.35);
  background: linear-gradient(180deg, #221c12 0%, #100e0a 100%);
  color: var(--ls-text);
  cursor: pointer;
  font-size: 12.5px;
  font-weight: 700;
  letter-spacing: 0.4px;
  transition: all 0.28s cubic-bezier(.2,.8,.2,1);
  overflow: hidden;
  box-shadow: 0 2px 8px rgba(0,0,0,0.3), inset 0 1px 0 rgba(255,240,176,0.06);
}
.ls-btn::before {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(105deg, transparent 25%, rgba(255,240,176,0.18) 50%, transparent 75%);
  background-size: 200% 100%;
  opacity: 0;
  transition: opacity 0.2s;
}
.ls-btn::after {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: 9px;
  padding: 1px;
  background: linear-gradient(135deg, transparent, rgba(255,240,176,0.4), transparent);
  -webkit-mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  mask: linear-gradient(#000 0 0) content-box, linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask-composite: exclude;
  opacity: 0;
  transition: opacity 0.25s;
}
.ls-btn:hover {
  border-color: var(--ls-gold-bright);
  color: var(--ls-gold-bright);
  box-shadow: 0 0 20px rgba(232,198,90,0.3), 0 4px 12px rgba(0,0,0,0.35);
  transform: translateY(-2px);
  text-shadow: 0 0 10px rgba(255,240,176,0.35);
}
.ls-btn:hover::before {
  opacity: 1;
  animation: ls-shimmer 1.2s linear infinite;
}
.ls-btn:hover::after { opacity: 1; }
.ls-btn-active {
  border-color: var(--ls-gold-bright);
  color: #1a1408;
  background: linear-gradient(135deg, #fff6c8 0%, var(--ls-gold) 45%, var(--ls-amber) 100%);
  box-shadow: 0 0 22px rgba(255,240,176,0.5), 0 4px 10px rgba(0,0,0,0.35);
  animation: ls-breathe 2.6s ease-in-out infinite;
  text-shadow: 0 1px 0 rgba(255,255,255,0.3);
}
.ls-btn-active:hover {
  color: #1a1408;
  border-color: #fff6c8;
  transform: translateY(-2px) scale(1.03);
}
.ls-btn-dim {
  opacity: 0.7;
  color: var(--ls-text-dim);
}
.ls-add-row {
  margin-top: 14px;
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}
.ls-btn-add {
  padding: 8px 18px;
  border-radius: 9px;
  border: 1px dashed rgba(168,212,122,0.5);
  background: rgba(40,54,26,0.35);
  color: var(--ls-ok);
  cursor: pointer;
  font-size: 13px;
  font-weight: 600;
  font-family: inherit;
  transition: all 0.25s ease;
}
.ls-btn-add:hover:not(:disabled) {
  border-color: var(--ls-ok);
  box-shadow: 0 0 16px rgba(168,212,122,0.3);
  transform: translateY(-1px);
}
.ls-btn-add:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.ls-btn-remove {
  border-radius: 7px;
  border: 1px solid rgba(239,122,104,0.4);
  background: rgba(90,30,24,0.4);
  color: var(--ls-danger);
  cursor: pointer;
  font-family: inherit;
  transition: all 0.25s ease;
}
.ls-btn-remove:hover {
  border-color: var(--ls-danger);
  box-shadow: 0 0 12px rgba(239,122,104,0.35);
}
.ls-btn-probe {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 6px 16px;
  border-radius: 8px;
  border: 1px solid rgba(232,198,90,0.5);
  background: linear-gradient(180deg, rgba(232,198,90,0.16) 0%, rgba(16,12,8,0.9) 100%);
  color: var(--ls-gold-bright);
  cursor: pointer;
  font-size: 12px;
  font-weight: 700;
  letter-spacing: 0.4px;
  transition: all 0.28s ease;
  box-shadow: 0 0 12px rgba(232,198,90,0.12), inset 0 1px 0 rgba(255,240,176,0.1);
  position: relative;
  overflow: hidden;
}
.ls-btn-probe:hover {
  background: linear-gradient(180deg, rgba(255,240,176,0.28) 0%, rgba(40,30,12,0.95) 100%);
  border-color: var(--ls-gold-bright);
  box-shadow: 0 0 22px rgba(255,240,176,0.35);
  color: #fff8d0;
  transform: translateY(-1px);
}
.ls-btn-probe-icon {
  display: inline-block;
  font-size: 11px;
  color: var(--ls-gold-bright);
  text-shadow: 0 0 10px rgba(255,240,176,0.7);
  animation: ls-pulse 2.5s ease-in-out infinite;
}

/* Probe */
.ls-probe { margin: 10px 0 14px; }
.ls-probe-result { font-size: 11.5px; margin-left: 10px; font-weight: 600; }
.ls-probe-ok { color: var(--ls-ok); text-shadow: 0 0 8px rgba(168,212,122,0.35); }
.ls-probe-fail { color: var(--ls-danger); text-shadow: 0 0 8px rgba(239,122,104,0.35); }
.ls-probe-select-row {
  margin-top: 10px;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.ls-probe-select-row span { font-size: 12px; color: var(--ls-text-dim); }
.ls-select {
  padding: 5px 10px;
  border-radius: 8px;
  border: 1px solid var(--ls-border);
  background: linear-gradient(180deg, #14110c, #0a0907);
  color: var(--ls-text);
  font-size: 12.5px;
  outline: none;
  cursor: pointer;
  max-width: 100%;
  transition: all 0.2s;
}
.ls-select:hover, .ls-select:focus {
  border-color: var(--ls-gold-bright);
  box-shadow: 0 0 14px rgba(232,198,90,0.25);
}
.ls-select option { background: #14110c; color: var(--ls-text); }

/* Details */
.ls-details {
  margin: 10px 0;
  border: 1px solid rgba(232,198,90,0.18);
  border-radius: 12px;
  padding: 0;
  background:
    linear-gradient(155deg, rgba(232,198,90,0.07) 0%, transparent 45%),
    linear-gradient(180deg, #16120c, #0e0c09);
  overflow: hidden;
  transition: all 0.3s ease;
  box-shadow: 0 2px 12px rgba(0,0,0,0.25);
}
.ls-details:hover {
  border-color: rgba(232,198,90,0.4);
  box-shadow: 0 0 24px rgba(232,198,90,0.1);
}
.ls-details[open] {
  border-color: rgba(255,240,176,0.4);
  box-shadow: 0 0 28px rgba(232,198,90,0.14), inset 0 0 30px rgba(232,198,90,0.03);
}
.ls-details summary {
  cursor: pointer;
  font-weight: 800;
  font-size: 13.5px;
  color: var(--ls-text);
  padding: 12px 16px;
  user-select: none;
  list-style: none;
  display: flex;
  align-items: center;
  gap: 10px;
  transition: all 0.25s;
  letter-spacing: 0.3px;
}
.ls-details summary::-webkit-details-marker { display: none; }
.ls-details summary:hover {
  color: var(--ls-gold-bright);
  background: linear-gradient(90deg, rgba(232,198,90,0.1), transparent);
  text-shadow: 0 0 12px rgba(255,240,176,0.3);
}
.ls-arrow {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px; height: 18px;
  font-size: 9px;
  color: #1a1408;
  background: linear-gradient(135deg, var(--ls-gold-bright), var(--ls-gold));
  border-radius: 4px;
  transition: transform 0.3s cubic-bezier(.2,.8,.2,1);
  box-shadow: 0 0 10px rgba(232,198,90,0.35);
  flex-shrink: 0;
}
.ls-details[open] .ls-arrow {
  transform: rotate(90deg);
  box-shadow: 0 0 14px rgba(255,240,176,0.55);
}
.ls-details-content {
  padding: 6px 16px 16px;
  border-top: 1px solid rgba(232,198,90,0.15);
  background: linear-gradient(180deg, rgba(232,198,90,0.03), transparent);
}

/* Feature strip */
.ls-feature-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 18px;
  justify-content: center;
}
.ls-feature-chip {
  position: relative;
  font-size: 11px;
  padding: 5px 14px;
  border-radius: 999px;
  border: 1px solid rgba(232,198,90,0.45);
  color: var(--ls-gold-bright);
  background: linear-gradient(135deg, rgba(232,198,90,0.14), rgba(20,16,8,0.6));
  letter-spacing: 1px;
  font-weight: 700;
  overflow: hidden;
  box-shadow: 0 0 12px rgba(232,198,90,0.1);
  transition: all 0.25s;
  text-shadow: 0 0 8px rgba(255,240,176,0.25);
}
.ls-feature-chip::after {
  content: '';
  position: absolute;
  top: 0; left: -60%;
  width: 40%; height: 100%;
  background: linear-gradient(90deg, transparent, rgba(255,255,255,0.2), transparent);
  transform: skewX(-20deg);
  animation: ls-chip-shine 3.5s ease-in-out infinite;
}
.ls-feature-chip:hover {
  border-color: var(--ls-gold-bright);
  box-shadow: 0 0 18px rgba(255,240,176,0.35);
  transform: translateY(-2px);
  color: #fff8d0;
}

/* Seal divider */
.ls-seal {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 14px;
  margin: 20px 0 8px;
  color: var(--ls-gold);
  font-size: 13px;
  opacity: 0.75;
}
.ls-seal::before,
.ls-seal::after {
  content: '';
  flex: 1;
  height: 1px;
  background: linear-gradient(90deg, transparent, var(--ls-gold-bright), transparent);
  box-shadow: 0 0 8px rgba(255,240,176,0.35);
}
.ls-seal-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px; height: 28px;
  border-radius: 50%;
  border: 1px solid rgba(232,198,90,0.4);
  background: radial-gradient(circle at 40% 35%, rgba(255,240,176,0.2), rgba(10,8,6,0.9));
  box-shadow: 0 0 16px rgba(232,198,90,0.25);
  animation: ls-seal-spin 16s linear infinite;
  font-size: 13px;
  text-shadow: 0 0 8px rgba(255,240,176,0.5);
}

/* AntDesign overrides */
.ls-container .ant-input,
.ls-container .ant-input-affix-wrapper {
  background: linear-gradient(180deg, #12100c, #0a0907) !important;
  border-color: rgba(232,198,90,0.28) !important;
  color: var(--ls-text) !important;
  border-radius: 9px !important;
  transition: border-color 0.2s, box-shadow 0.2s !important;
  box-shadow: inset 0 1px 4px rgba(0,0,0,0.35) !important;
}
.ls-container .ant-input::placeholder {
  color: var(--ls-text-dim) !important;
  opacity: 0.7 !important;
}
.ls-container .ant-input:hover,
.ls-container .ant-input-affix-wrapper:hover {
  border-color: var(--ls-gold) !important;
  box-shadow: 0 0 12px rgba(232,198,90,0.15), inset 0 1px 4px rgba(0,0,0,0.35) !important;
}
.ls-container .ant-input:focus,
.ls-container .ant-input-focused,
.ls-container .ant-input-affix-wrapper-focused,
.ls-container .ant-input-affix-wrapper:focus {
  border-color: var(--ls-gold-bright) !important;
  box-shadow: 0 0 0 2px rgba(232,198,90,0.2), 0 0 18px rgba(255,240,176,0.15) !important;
}
.ls-container .ant-input-password-icon,
.ls-container .anticon {
  color: var(--ls-gold) !important;
}
.ls-container .ant-input,
.ls-container .ant-input-affix-wrapper input {
  font-size: 13px !important;
}
.ls-container .ant-input-affix-wrapper > input.ant-input {
  background: transparent !important;
  color: var(--ls-text) !important;
}
.ls-groups {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.ls-group-card {
  border: 1px solid var(--ls-border-soft);
  border-radius: 12px;
  padding: 12px 14px 14px;
  background: linear-gradient(180deg, rgba(232,198,90,0.06), rgba(0,0,0,0.18));
  box-shadow: 0 2px 12px rgba(0,0,0,0.25);
  transition: border-color .2s, box-shadow .2s, opacity .2s;
}
.ls-group-card:hover {
  border-color: var(--ls-border);
}
.ls-group-card-dragging {
  opacity: 0.45;
  border-color: var(--ls-gold-dim);
  box-shadow: 0 0 0 1px var(--ls-gold-dim), 0 0 24px rgba(232,198,90,0.22);
}
.ls-group-card-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 10px;
}
/* 拖动排序手柄：卡片内唯一可拖动元素（醒目小按钮），按住它才发起拖动，
   避免在输入框内按下鼠标选择文本时被浏览器误判为拖动卡片 */
.ls-drag-handle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: 0 0 auto;
  width: 26px;
  height: 26px;
  border-radius: 7px;
  border: 1px solid var(--ls-gold-dim);
  background: linear-gradient(180deg, rgba(255,240,176,0.22), rgba(90,74,40,0.42));
  color: var(--ls-gold-bright);
  font-size: 14px;
  line-height: 1;
  cursor: grab;
  user-select: none;
  -webkit-user-select: none;
  box-shadow: 0 0 10px rgba(232,198,90,0.18), inset 0 1px 0 rgba(255,240,176,0.28);
  transition: border-color .2s, box-shadow .2s, transform .2s, color .2s, background .2s;
}
.ls-drag-handle:hover {
  border-color: var(--ls-gold-bright);
  color: #fff8d0;
  background: linear-gradient(180deg, rgba(255,240,176,0.34), rgba(120,96,48,0.45));
  box-shadow: 0 0 18px rgba(255,240,176,0.42), inset 0 1px 0 rgba(255,240,176,0.4);
  transform: translateY(-1px);
}
.ls-drag-handle:active {
  cursor: grabbing;
  transform: translateY(0) scale(0.94);
  box-shadow: 0 0 22px rgba(255,240,176,0.5), inset 0 1px 0 rgba(255,240,176,0.4);
}
/* 上移/下移按钮：与拖动等价的排序入口，供不便拖动时使用 */
.ls-move-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex: 0 0 auto;
  width: 22px;
  height: 22px;
  padding: 0;
  border-radius: 6px;
  border: 1px solid var(--ls-border-soft);
  background: rgba(40,32,16,0.5);
  color: var(--ls-text-dim);
  font-size: 12px;
  line-height: 1;
  cursor: pointer;
  user-select: none;
  -webkit-user-select: none;
  font-family: inherit;
  transition: border-color .2s, color .2s, background .2s, box-shadow .2s, transform .2s;
}
.ls-move-btn:hover:not(:disabled) {
  border-color: var(--ls-gold-dim);
  color: var(--ls-gold-bright);
  background: linear-gradient(180deg, rgba(255,240,176,0.24), rgba(90,74,40,0.42));
  box-shadow: 0 0 12px rgba(232,198,90,0.32);
  transform: translateY(-1px);
}
.ls-move-btn:active:not(:disabled) {
  transform: translateY(0) scale(0.94);
}
.ls-move-btn:disabled {
  opacity: 0.3;
  cursor: not-allowed;
}
.ls-group-card-title {
  flex: 1;
  font-weight: 600;
  font-size: 13.5px;
  color: var(--ls-text);
  letter-spacing: 0.5px;
}
.ls-badge-main {
  background: linear-gradient(135deg, rgba(232,198,90,0.32), rgba(232,198,90,0.12));
  border-color: var(--ls-gold-dim);
  color: var(--ls-gold-bright);
  text-shadow: 0 0 10px rgba(255,240,176,0.5);
  box-shadow: 0 0 14px rgba(232,198,90,0.25);
}

/* ==================== 信息层级（5.0.0 排版优化） ==================== */

/* 唯一强提醒：一条就够，其余说明收进底部帮助 */
.ls-requirement {
  display: flex;
  align-items: flex-start;
  gap: 9px;
  margin: 0 0 16px;
  padding: 10px 14px;
  border-radius: 10px;
  border: 1px solid rgba(232,198,90,0.42);
  background: linear-gradient(120deg, rgba(232,198,90,0.16), rgba(232,198,90,0.04));
  font-size: 12px;
  color: var(--ls-gold-bright);
  line-height: 1.65;
}
.ls-requirement-icon { flex: 0 0 auto; font-size: 13px; line-height: 1.5; }

/* 概览卡：当前渠道 + 状态指标 + 一键切换 */
.ls-hero {
  margin-bottom: 6px;
  padding: 16px 16px 14px;
  border-radius: 14px;
  border: 1px solid rgba(232,198,90,0.22);
  background:
    radial-gradient(ellipse 70% 120% at 0% 0%, rgba(232,198,90,0.14), transparent 60%),
    linear-gradient(160deg, rgba(30,24,14,0.96), rgba(10,9,7,0.98));
  box-shadow: 0 6px 24px rgba(0,0,0,0.35), inset 0 1px 0 rgba(255,240,176,0.06);
}
.ls-hero-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.ls-hero-kicker { font-size: 10.5px; letter-spacing: 2px; color: var(--ls-text-mute); }
.ls-hero-title {
  font-size: 16px;
  font-weight: 800;
  color: var(--ls-gold-bright);
  letter-spacing: 0.5px;
  text-shadow: 0 0 14px rgba(255,240,176,0.3);
}
.ls-hero-desc { margin-top: 6px; font-size: 11.5px; color: var(--ls-text-dim); line-height: 1.6; }
.ls-hero-metrics { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 12px; }
.ls-metric {
  flex: 1 1 110px;
  padding: 8px 10px;
  border-radius: 9px;
  border: 1px solid rgba(232,198,90,0.14);
  background: rgba(0,0,0,0.28);
}
.ls-metric-k { font-size: 10px; letter-spacing: 1.2px; color: var(--ls-text-mute); }
.ls-metric-v { margin-top: 3px; font-size: 13px; font-weight: 700; color: var(--ls-text); word-break: break-all; }

/* 区块标题：一个主标题 + 一句右侧说明，替代长段落 */
.ls-block-title {
  display: flex;
  align-items: baseline;
  gap: 10px;
  flex-wrap: wrap;
  margin: 22px 0 10px;
  padding-bottom: 8px;
  border-bottom: 1px solid rgba(232,198,90,0.16);
}
.ls-block-title .ls-block-name {
  font-size: 13px;
  font-weight: 800;
  letter-spacing: 1px;
  color: var(--ls-gold-bright);
}
.ls-block-title .ls-block-note { font-size: 11px; color: var(--ls-text-mute); }

/* 渠道组卡片头：整行可点开/收起 */
.ls-card-head2 {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  cursor: pointer;
  user-select: none;
}
.ls-card-index { font-size: 11px; letter-spacing: 1px; color: var(--ls-text-mute); }
.ls-card-name { font-size: 13.5px; font-weight: 700; color: var(--ls-text); }
.ls-card-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 7px;
  font-size: 11.5px;
  color: var(--ls-text-mute);
}
.ls-card-meta code {
  font-family: ui-monospace, Consolas, monospace;
  color: var(--ls-text-dim);
  background: rgba(255,255,255,0.04);
  padding: 1px 5px;
  border-radius: 4px;
}
.ls-dot { width: 7px; height: 7px; border-radius: 50%; flex: 0 0 auto; }
.ls-dot-ok { background: #a8d47a; box-shadow: 0 0 8px rgba(168,212,122,0.7); }
.ls-dot-off { background: #ef7a68; box-shadow: 0 0 8px rgba(239,122,104,0.6); }
.ls-chevron {
  margin-left: auto;
  font-size: 10px;
  color: var(--ls-gold-dim);
  transition: transform .25s ease;
}
.ls-chevron-open { transform: rotate(90deg); }

/* 卡片内分组：连接 / 能力 / 高级参数 */
.ls-subgroup {
  margin-top: 14px;
  padding-top: 10px;
  border-top: 1px dashed rgba(232,198,90,0.16);
}
.ls-subgroup-first { margin-top: 4px; padding-top: 0; border-top: none; }
.ls-subgroup-title {
  font-size: 11px;
  letter-spacing: 1.6px;
  color: var(--ls-gold-dim);
  margin-bottom: 6px;
}

/* 紧凑开关行：标题 + 一行说明 */
.ls-toggle-compact {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  margin-bottom: 8px;
  padding: 9px 12px;
  border-radius: 9px;
  border: 1px solid rgba(232,198,90,0.14);
  background: rgba(0,0,0,0.22);
  transition: border-color .2s, background .2s;
}
.ls-toggle-compact:hover { border-color: rgba(232,198,90,0.3); background: rgba(0,0,0,0.3); }
.ls-toggle-compact input { margin-top: 2px; }
.ls-tc-title { font-size: 12.5px; font-weight: 600; color: var(--ls-text); line-height: 1.5; }
.ls-tc-desc { font-size: 10.5px; color: var(--ls-text-mute); line-height: 1.55; margin-top: 2px; }

/* 受控折叠区（不用 details，避免重渲染后自动收起） */
.ls-fold {
  margin-top: 10px;
  border-radius: 10px;
  border: 1px solid rgba(232,198,90,0.14);
  background: rgba(0,0,0,0.18);
  overflow: hidden;
}
.ls-fold-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 10px 12px;
  border: none;
  background: transparent;
  color: var(--ls-text-dim);
  font-size: 12px;
  font-weight: 600;
  font-family: inherit;
  text-align: left;
  cursor: pointer;
  transition: color .2s, background .2s;
}
.ls-fold-btn:hover { color: var(--ls-gold-bright); background: rgba(232,198,90,0.06); }
.ls-fold-arrow { font-size: 9px; color: var(--ls-gold-dim); transition: transform .25s ease; }
.ls-fold-open .ls-fold-arrow { transform: rotate(90deg); }
.ls-fold-body { padding: 4px 12px 12px; border-top: 1px solid rgba(232,198,90,0.12); }

/* 帮助列表 */
.ls-help-list { margin: 0; padding-left: 18px; }
.ls-help-list li { font-size: 11.5px; color: var(--ls-text-dim); line-height: 1.85; }
.ls-help-list li b { color: var(--ls-gold-bright); font-weight: 700; }
.ls-help-list li code {
  font-family: ui-monospace, Consolas, monospace;
  background: rgba(255,255,255,0.05);
  padding: 0 4px;
  border-radius: 3px;
}
.ls-note { font-size: 10.5px; color: var(--ls-text-mute); line-height: 1.55; margin: 4px 0 10px 2px; }

";


    protected override void OnInitialized()
    {
        if (!_subscribed)
        {
            LanguageModelRouter.OnGroupChanged += OnGroupChangedHandler;
            _subscribed = true;
        }
    }

    void OnGroupChangedHandler() => InvokeAsync(StateHasChanged);

    // ModuleUIBase 未暴露 Dispose(bool)，改用显式 IDisposable 实现退订静态事件。
    // Blazor 渲染器释放组件时会检测 IDisposable 并调用（接口重新实现，基类是否实现该接口均可）。
    void IDisposable.Dispose()
    {
        if (_subscribed)
        {
            LanguageModelRouter.OnGroupChanged -= OnGroupChangedHandler;
            _subscribed = false;
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "Configuration NULL");
            return;
        }

        _seq = 0;

        b.OpenElement(_seq++, "style");
        b.AddContent(_seq++, Css);
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-container");

        b.OpenElement(_seq++, "div"); b.AddAttribute(_seq++, "class", "ls-border-glow"); b.CloseElement();
        b.OpenElement(_seq++, "div"); b.AddAttribute(_seq++, "class", "ls-frame"); b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-particles");
        // 24 颗浮游金尘：位置/延迟/时长/大小错开
        double[] pLeft = { 3, 7, 12, 18, 24, 30, 36, 42, 48, 54, 60, 66, 72, 78, 84, 90, 96, 9, 21, 45, 63, 81, 15, 88 };
        double[] pDelay = { 0, 0.4, 0.9, 1.3, 1.8, 2.2, 2.7, 3.1, 0.2, 0.7, 1.5, 2.0, 2.5, 3.4, 0.5, 1.1, 1.9, 2.9, 3.6, 0.8, 1.6, 2.4, 3.2, 3.8 };
        double[] pDur = { 6.5, 7.2, 8.0, 9.0, 7.5, 10.0, 8.5, 6.8, 9.5, 7.8, 11.0, 8.2, 9.2, 7.0, 10.5, 8.8, 6.2, 9.8, 7.4, 10.2, 8.6, 11.5, 7.6, 9.4 };
        string[] pSize = { "ls-particle-sm", "ls-particle-md", "ls-particle-lg", "ls-particle-sm", "ls-particle-xl", "ls-particle-md", "ls-particle-sm", "ls-particle-lg", "ls-particle-md", "ls-particle-sm", "ls-particle-xl", "ls-particle-md", "ls-particle-lg", "ls-particle-sm", "ls-particle-md", "ls-particle-sm", "ls-particle-lg", "ls-particle-md", "ls-particle-xl", "ls-particle-sm", "ls-particle-md", "ls-particle-lg", "ls-particle-sm", "ls-particle-md" };
        for (int pi = 0; pi < 24; pi++)
        {
            b.OpenElement(_seq++, "span");
            b.AddAttribute(_seq++, "class", $"ls-particle {pSize[pi]}");
            b.AddAttribute(_seq++, "style",
                $"left:{pLeft[pi].ToString(System.Globalization.CultureInfo.InvariantCulture)}%;" +
                $"animation-delay:{pDelay[pi].ToString(System.Globalization.CultureInfo.InvariantCulture)}s,{ (pDelay[pi] * 0.3).ToString(System.Globalization.CultureInfo.InvariantCulture)}s;" +
                $"animation-duration:{pDur[pi].ToString(System.Globalization.CultureInfo.InvariantCulture)}s,{(1.6 + pi % 5 * 0.25).ToString(System.Globalization.CultureInfo.InvariantCulture)}s;");
            b.CloseElement();
        }
        b.CloseElement();

        b.OpenElement(_seq++, "span"); b.AddAttribute(_seq++, "class", "ls-rune ls-rune-1"); b.AddContent(_seq++, "☯"); b.CloseElement();
        b.OpenElement(_seq++, "span"); b.AddAttribute(_seq++, "class", "ls-rune ls-rune-2"); b.AddContent(_seq++, "✦"); b.CloseElement();
        b.OpenElement(_seq++, "span"); b.AddAttribute(_seq++, "class", "ls-rune ls-rune-3"); b.AddContent(_seq++, "◈"); b.CloseElement();
        b.OpenElement(_seq++, "span"); b.AddAttribute(_seq++, "class", "ls-rune ls-rune-4"); b.AddContent(_seq++, "✧"); b.CloseElement();

        // ornate corners with gems
        foreach (var corner in new[] { "tl", "tr", "bl", "br" })
        {
            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "class", $"ls-corner ls-corner-{corner}");
            b.OpenElement(_seq++, "span");
            b.AddAttribute(_seq++, "class", "ls-corner-gem");
            b.CloseElement();
            b.CloseElement();
        }

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-inner");
        Panel(b);
        b.CloseElement();

        b.CloseElement();
    }

    void Panel(RenderTreeBuilder b)
    {
        // 标题 / 圣徽
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-title-wrap");

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-crest");
        b.OpenElement(_seq++, "div"); b.AddAttribute(_seq++, "class", "ls-crest-halo"); b.CloseElement();
        b.OpenElement(_seq++, "div"); b.AddAttribute(_seq++, "class", "ls-crest-ring"); b.CloseElement();
        b.OpenElement(_seq++, "div"); b.AddAttribute(_seq++, "class", "ls-crest-ring-inner"); b.CloseElement();
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-crest-core");
        b.AddContent(_seq++, "枢");
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-title-ornament");
        b.AddContent(_seq++, "◈  灵  枢  ◈");
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-title");
        b.AddContent(_seq++, "灵枢 · 语言模型路由");
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-title-sub");
        b.AddContent(_seq++, "—  切  换  ·  容  灾  ·  多  模  态  —");
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-title-bar");
        b.CloseElement();
        b.CloseElement();

        // 特性条（装饰，非业务文案）
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-feature-strip");
        FeatureChip(b, "多路渠道");
        FeatureChip(b, "自动容灾");
        FeatureChip(b, "智能思考");
        FeatureChip(b, "一句话切换");
        FeatureChip(b, "多模态");
        b.CloseElement();

        // 唯一强提醒（其余说明统一收进底部「使用帮助」，首屏不再堆文字）
        RequirementBanner(b);

        Configuration.EnsureGroups();
        EnsureDetectArrays();
        int[] order = LanguageModelRouter.GetGroupOrder(Configuration);

        // === 概览 + 切换（首屏只放最常用的信息与操作）===
        HeroCard(b, order);

        // === 渠道组（可增删，拖动/按钮排序，最上方为主组）===
        BlockTitle(b, "渠道组", "最上方为主渠道 · 失败按顺序往下试");
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-groups");
        for (int di = 0; di < order.Length; di++)
            GroupCard(b, di);

        // 添加渠道组
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-add-row");
        var canAdd = Configuration.Groups.Count < LanguageModelRouterConfig.MaxGroups;
        b.OpenElement(_seq++, "button");
        b.AddAttribute(_seq++, "type", "button");
        b.AddAttribute(_seq++, "class", "ls-btn-add");
        if (!canAdd)
            b.AddAttribute(_seq++, "disabled", true);
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, () =>
        {
            Configuration.AddGroup();
            EnsureDetectArrays();
            StateHasChanged();
        }));
        b.AddContent(_seq++, canAdd
            ? $"+ 添加渠道组（当前 {Configuration.Groups.Count}/{LanguageModelRouterConfig.MaxGroups}）"
            : $"已达上限 {LanguageModelRouterConfig.MaxGroups} 组");
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();

        // === 多模态（常驻 + 失败自愈）===
        BlockTitle(b, "多模态", "图片/视频/音频 · 失败自动去图重试");
        AddMultimodalSection(b);

        // === 自定义命令 ===
        BlockTitle(b, "自定义命令", "默认关闭 · QQ 环境只认主人");
        AddCommandSection(b);

        // === 容灾与思考 ===
        BlockTitle(b, "容灾与思考", "默认设置即可，进阶项已折叠");
        AutoFailoverToggle(b);
        AddThinkingAdvanced(b);
        AddDisasterAdvanced(b);

        // === 安全 ===
        BlockTitle(b, "安全");
        AddEncryptionToggle(b);
        AddSslToggle(b);

        // === 使用帮助（默认收起，需要时再看）===
        HelpSection(b);
    }

    void FeatureChip(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-feature-chip");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    // ==================== 布局辅助（排版优化） ====================

    /// <summary>首屏唯一强提醒：使用前必须做的配置动作。</summary>
    void RequirementBanner(RenderTreeBuilder b)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-requirement");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-requirement-icon");
        b.AddContent(_seq++, "✦");
        b.CloseElement();
        b.OpenElement(_seq++, "span");
        b.AddContent(_seq++, "使用前：在角色配置里关闭「OpenAI语言模型」，改为勾选本模块。");
        b.CloseElement();
        b.CloseElement();
    }

    /// <summary>概览卡：当前渠道 + 三项状态 + 一键切换。</summary>
    void HeroCard(RenderTreeBuilder b, int[] order)
    {
        LanguageModelRouterConfig cfg = Configuration!;
        bool auto = cfg.ForcedGroupIndex < 0;
        int total = cfg.Groups.Count;
        int configured = order.Count(g => IsGroupConfigured(g));

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-hero");

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-hero-row");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-hero-kicker");
        b.AddContent(_seq++, auto ? "当前渠道 · 自动" : "当前渠道 · 已锁定");
        b.CloseElement();
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-hero-title");
        b.AddContent(_seq++, GetActiveGroupLabel());
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-hero-desc");
        b.AddContent(_seq++, auto
            ? "从最上面的渠道开始尝试，失败自动换下一组，不用你操心。"
            : "已固定使用这一组；该组失败时仍会自动切到备用渠道（需开启自动容灾）。");
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-hero-metrics");
        Metric(b, "渠道组", $"{configured} / {total} 已配置");
        Metric(b, "模式", auto ? "自动容灾" : $"锁定第 {cfg.ForcedGroupIndex + 1} 组");
        Metric(b, "思考过程", cfg.ShowThinkingChain ? "显示" : "隐藏");
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-btn-row");
        b.AddAttribute(_seq++, "style", "margin-top:12px; margin-bottom:0;");
        for (int di = 0; di < order.Length; di++)
        {
            int group = order[di];
            string name = LanguageModelRouter.GetGroupName(group, Configuration);
            string mainMark = di == 0 ? " ★主" : "";
            string btnLabel = string.IsNullOrWhiteSpace(name) ? $"第{group + 1}组{mainMark}" : $"第{group + 1}组({name}){mainMark}";
            AddSwitchBtn(b, btnLabel, cfg.ForcedGroupIndex == group, IsGroupConfigured(group), () => SwitchTo(group));
        }
        AddSwitchBtn(b, "自动容灾", cfg.ForcedGroupIndex < 0, true, () => SwitchTo(-1));
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-note");
        b.AddContent(_seq++, "也可以直接对桌宠说「切换到第2组」或「切到硅基」，AI 会自己换。");
        b.CloseElement();

        b.CloseElement();
    }

    void Metric(RenderTreeBuilder b, string key, string value)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-metric");
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-metric-k");
        b.AddContent(_seq++, key);
        b.CloseElement();
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-metric-v");
        b.AddContent(_seq++, value);
        b.CloseElement();
        b.CloseElement();
    }

    /// <summary>区块标题：主标题 + 可选的一句右侧注解，替代整段说明文字。</summary>
    void BlockTitle(RenderTreeBuilder b, string name, string note = "")
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-block-title");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-block-name");
        b.AddContent(_seq++, name);
        b.CloseElement();
        if (string.IsNullOrEmpty(note) == false)
        {
            b.OpenElement(_seq++, "span");
            b.AddAttribute(_seq++, "class", "ls-block-note");
            b.AddContent(_seq++, note);
            b.CloseElement();
        }
        b.CloseElement();
    }

    /// <summary>紧凑开关：标题 + 一行说明，替代"复选框 + 长句"的写法。</summary>
    void CompactSwitch(RenderTreeBuilder b, string id, bool value, Action<bool> onChange, string title, string desc)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-toggle-compact");
        b.OpenElement(_seq++, "input");
        b.AddAttribute(_seq++, "type", "checkbox");
        b.AddAttribute(_seq++, "id", id);
        b.AddAttribute(_seq++, "checked", value);
        b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            onChange(e.Value is bool bv ? bv : !value);
            StateHasChanged();
        }));
        b.CloseElement();

        b.OpenElement(_seq++, "label");
        b.AddAttribute(_seq++, "for", id);
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-tc-title");
        b.AddContent(_seq++, title);
        b.CloseElement();
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-tc-desc");
        b.AddContent(_seq++, desc);
        b.CloseElement();
        b.CloseElement();
        b.CloseElement();
    }

    /// <summary>
    /// 受控折叠区：按钮 + div 实现（不用原生 details），状态存于 _foldStates，
    /// 任何 StateHasChanged 重渲染后都保持原展开状态。
    /// </summary>
    void Fold(RenderTreeBuilder b, string key, string title, Action renderContent)
    {
        bool open = _foldStates.TryGetValue(key, out bool saved) && saved;

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", open ? "ls-fold ls-fold-open" : "ls-fold");

        b.OpenElement(_seq++, "button");
        b.AddAttribute(_seq++, "type", "button");
        b.AddAttribute(_seq++, "class", "ls-fold-btn");
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, () =>
        {
            _foldStates[key] = !open;
            StateHasChanged();
        }));
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-fold-arrow");
        b.AddContent(_seq++, "▶");
        b.CloseElement();
        b.AddContent(_seq++, title);
        b.CloseElement();

        if (open)
        {
            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "class", "ls-fold-body");
            renderContent();
            b.CloseElement();
        }

        b.CloseElement();
    }

    /// <summary>使用帮助：给第一次使用的用户看的 5 步 + 可展开的进阶说明。</summary>
    void HelpSection(RenderTreeBuilder b)
    {
        Fold(b, "help", "使用帮助（第一次用点这里）", () =>
        {
            b.OpenElement(_seq++, "ol");
            b.AddAttribute(_seq++, "class", "ls-help-list");
            HelpItem(b, "在角色配置里关闭「OpenAI语言模型」，勾选本模块（灵枢）。");
            HelpItem(b, "最上面一组必填三项：API 地址、模型 ID、API Key；点「探测」可自动拉取模型列表。");
            HelpItem(b, "想容灾就再加一组、排在下面：失败时按从上到下的顺序自动换组。");
            HelpItem(b, "对桌宠说「切换到第2组」「切到硅基」，或点上面的按钮手动切换。");
            HelpItem(b, "需要看图：在支持图片的组里打开「原生多模态」，保存配置后重载一次本插件。");
            HelpItem(b, "想让桌宠记住这张图：保持「允许图片常驻上下文」开启，AI 会用 persistent=true 把图留在上下文里，直到这段对话被记忆压缩归档。");
            b.CloseElement();

            Fold(b, "help-advanced", "进阶说明", () =>
            {
                b.OpenElement(_seq++, "ul");
                b.AddAttribute(_seq++, "class", "ls-help-list");
                HelpItem(b, "什么时候换组：限流（429）、额度用完（401/403/积分不足）、服务器错误（5xx）、超时、网络不通，或返回内容命中错误关键字。");
                HelpItem(b, "最上面的组是主渠道；拖动左上角「⠿」或点卡片右侧「↑↓」都能调整顺序。");
                HelpItem(b, "智能思考：开启后平时不思考（更快更省），需要调用工具等复杂任务时自动思考。");
                HelpItem(b, "Extra Headers / Extra Body 是 JSON，例如 {\"temperature\":0.7}，留空即不发送。");
                HelpItem(b, "API Key 默认用 Windows 加密保存，只有当前用户能解密；关闭加密则明文存于配置文件，便于迁移。");
                HelpItem(b, "自定义命令默认关闭：开启后在对话框输入 /重新生成 或 /删除最新一轮对话。QQ 群聊需在「自定义命令」里填主人 QQ，只有主人发的命令才会执行。");
                HelpItem(b, "图片被上游拒绝时会自动去掉图片重试一次，并对该组短时冷却，不会再出现「之后所有图都看不了」。");
                b.CloseElement();
            });
        });
    }

    void HelpItem(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "li");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    /// <summary>确保探测结果数组与当前组数对齐（增删组后调用）</summary>
    void EnsureDetectArrays()
    {
        int n = Configuration!.Groups.Count;
        if (_detectResults.Length != n)
        {
            var dr = new string?[n];
            var dm = new List<string>?[n];
            int copy = Math.Min(n, _detectResults.Length);
            Array.Copy(_detectResults, dr, copy);
            Array.Copy(_detectedModels, dm, copy);
            _detectResults = dr;
            _detectedModels = dm;
        }
    }

    bool IsGroupConfigured(int g)
    {
        var cfg = Configuration!;
        cfg.EnsureGroups();
        if (g < 0 || g >= cfg.Groups.Count) return false;
        return cfg.Groups[g].IsConfigured;
    }

    /// <summary>槽位是否开启了任意多模态（原生多模态或音频输入；组卡片徽标用）</summary>
    bool IsNativeEnabledSlot(int g)
    {
        var cfg = Configuration!;
        cfg.EnsureGroups();
        if (g < 0 || g >= cfg.Groups.Count) return false;
        return cfg.Groups[g].EnableNativeMultimodal || cfg.Groups[g].EnableAudioMultimodal;
    }

    // ==================== Group Config ====================

    void GroupConfig(RenderTreeBuilder b, int g, bool isPrimary)
    {
        var cfg = Configuration!;
        cfg.EnsureGroups();
        if (g < 0 || g >= cfg.Groups.Count) return;
        var ch = cfg.Groups[g];

        // ── 连接（新手只需要填这一块）──
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-subgroup ls-subgroup-first");
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-subgroup-title");
        b.AddContent(_seq++, "连 接");
        b.CloseElement();

        AddInput(b, "组名称（可留空，供 AI 识别）", ch.GroupName ?? "", v => ch.GroupName = v);
        AddInput(b, "API 地址", ch.Endpoint ?? "", v => ch.Endpoint = v);
        Note(b, "例如 https://api.openai.com/v1");

        AddInput(b, "模型 ID", ch.ModelId ?? "", v => ch.ModelId = v);

        // API Key：加密开关开启时以 DPAPI 密文存储（显示解密、输入加密）；关闭时与官方插件一致明文保存。
        // EnsureGroups 已按开关统一转换存储形态，UnprotectSecret 对明文原样返回，读取恒安全。
        AddPassword(b, "API Key", LanguageModelRouterConfig.UnprotectSecret(ch.ApiKey),
            v => ch.ApiKey = cfg.EncryptApiKeys
                ? (LanguageModelRouterConfig.ProtectSecret(v) ?? "")
                : v);
        Note(b, cfg.EncryptApiKeys ? "已用 Windows 加密保存，仅本机可解密" : "以明文保存，请注意保护配置文件");

        // 探测放在 Key 之后：探测需要鉴权，先填完三项再点才不至于报未授权
        ProbeSection(b, g);
        b.CloseElement();

        // ── 能力 ──
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-subgroup");
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-subgroup-title");
        b.AddContent(_seq++, "能 力");
        b.CloseElement();

        CompactSwitch(b, $"nativeMultimodal_{g}", ch.EnableNativeMultimodal,
            v => ch.EnableNativeMultimodal = v,
            "原生多模态", "图片 / 视频直接发给模型看（需模型本身支持，纯文本模型请关闭）");
        CompactSwitch(b, $"audioMultimodal_{g}", ch.EnableAudioMultimodal,
            v => ch.EnableAudioMultimodal = v,
            "音频输入", "把音频直接发给模型听（少数模型支持，默认关闭）");
        CompactSwitch(b, $"smartThinking_{g}", ch.SmartThinkingEnabled,
            v => ch.SmartThinkingEnabled = v,
            "智能思考（本组独立）", "平时不思考，更快更省；调用工具等复杂任务时自动思考");
        b.CloseElement();

        // ── 高级参数（默认收起，避免干扰新手）──
        Fold(b, $"group:{g}:adv", "更多模型参数", () =>
        {
            AddInput(b, "Reasoning Effort", ch.ReasoningEffort ?? "", v => ch.ReasoningEffort = string.IsNullOrWhiteSpace(v) ? null : v);
            Note(b, "推理强度：low / medium / high，留空则不发送");

            AddInput(b, "Temperature（采样温度）", FormatTemperature(ch.Temperature), v => ch.Temperature = ParseTemperature(v));
            Note(b, "越低越严谨、越高越发散（常用 0.6）；留空用服务端默认值");

            AddInput(b, "Extra Headers（JSON）", ch.ExtraHeaders ?? "", v => ch.ExtraHeaders = string.IsNullOrWhiteSpace(v) ? null : v);
            Note(b, "额外请求头，如 {\"X-Custom\":\"value\"}");

            AddInput(b, "Extra Body（JSON，思考模式）", ch.ExtraBody ?? "", v => ch.ExtraBody = string.IsNullOrWhiteSpace(v) ? null : v);
            Note(b, "思考模式下附加的请求体，如 {\"temperature\":0.7}");

            AddInput(b, "Extra Body（JSON，非思考模式）", ch.ExtraBodyNotThinking ?? "", v => ch.ExtraBodyNotThinking = string.IsNullOrWhiteSpace(v) ? null : v);
            Note(b, "留空则默认发送 {\"thinking\":{\"type\":\"disabled\"}}，明确要求模型关闭思考");
        });
    }

    /// <summary>输入项下方的一行浅色注解（比 AddHint 更轻，减少视觉噪音）。</summary>
    void Note(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-note");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    // ==================== 拖动排序 ====================

    /// <summary>渲染一张渠道组卡片：卡片头一眼看懂「第几组 / 名称 / 模型 / 是否就绪」，点击头部展开配置。</summary>
    void GroupCard(RenderTreeBuilder b, int displayIndex)
    {
        int slot = GetDisplaySlot(displayIndex);
        bool isPrimary = displayIndex == 0;
        bool isCurrent = IsCurrentSlot(slot);
        bool configured = IsGroupConfigured(slot);

        // 折叠状态由 _groupOpenStates 受控：未记录时按"主组默认展开、备用组默认收起"
        var cardConfig = Configuration!;
        GroupChannel? channel = slot >= 0 && slot < cardConfig.Groups.Count ? cardConfig.Groups[slot] : null;
        bool groupOpen = channel != null && _groupOpenStates.TryGetValue(channel, out bool savedOpen)
            ? savedOpen
            : isPrimary;

        string groupName = channel?.GroupName ?? "";
        string modelId = channel?.ModelId ?? "";
        string host = Uri.TryCreate(channel?.Endpoint, UriKind.Absolute, out Uri? endpointUri) ? endpointUri.Host : "";

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", _dragSource == displayIndex ? "ls-group-card ls-group-card-dragging" : "ls-group-card");
        // 卡片本身不可拖动：只有卡片头的拖拽手柄能发起拖动，
        // 这样在输入框内按下鼠标拖动只会选择文本，不会被误判为拖动排序
        b.AddAttribute(_seq++, "draggable", "false");
        // 卡片整体仍作为放置目标（拖到最上方即成为主渠道）
        b.AddAttribute(_seq++, "ondragover", EventCallback.Factory.Create<DragEventArgs>(this, e => { }));
        b.AddEventPreventDefaultAttribute(_seq++, "ondragover", true);
        b.AddAttribute(_seq++, "ondrop", EventCallback.Factory.Create<DragEventArgs>(this, e => DropGroup(displayIndex)));
        b.AddEventPreventDefaultAttribute(_seq++, "ondrop", true);

        // ── 卡片头（整行可点击展开/收起）──
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-card-head2");
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e =>
        {
            if (channel == null) return;
            _groupOpenStates[channel] = !groupOpen;
            StateHasChanged();
        }));

        // 拖拽手柄（卡片内唯一可拖动元素）：按住此按钮才发起拖动排序
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-drag-handle");
        b.AddAttribute(_seq++, "title", "按住拖动排序（拖到最上方即为主渠道）");
        b.AddAttribute(_seq++, "draggable", "true");
        b.AddAttribute(_seq++, "ondragstart", EventCallback.Factory.Create<DragEventArgs>(this, e => _dragSource = displayIndex));
        b.AddAttribute(_seq++, "ondragend", EventCallback.Factory.Create<DragEventArgs>(this, e => { _dragSource = null; StateHasChanged(); }));
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, e => { }));
        b.AddEventStopPropagationAttribute(_seq++, "onclick", true);
        b.AddContent(_seq++, "⠿");
        b.CloseElement();

        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-card-index");
        b.AddContent(_seq++, isPrimary ? "主组" : $"第{slot + 1}组");
        b.CloseElement();

        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-card-name");
        b.AddContent(_seq++, string.IsNullOrWhiteSpace(groupName)
            ? (isPrimary ? "未命名主渠道" : "未命名渠道")
            : groupName);
        b.CloseElement();

        if (isCurrent)
        {
            b.OpenElement(_seq++, "span");
            b.AddAttribute(_seq++, "class", "ls-badge ls-badge-active");
            b.AddContent(_seq++, "当前");
            b.CloseElement();
        }
        if (IsNativeEnabledSlot(slot))
        {
            b.OpenElement(_seq++, "span");
            b.AddAttribute(_seq++, "class", "ls-badge ls-badge-on");
            b.AddContent(_seq++, "✦ 多模态");
            b.CloseElement();
        }

        // 上移/下移按钮：与拖动等价的排序方式，首位禁用上移、末位禁用下移
        int groupCount = Configuration!.Groups.Count;
        b.OpenElement(_seq++, "button");
        b.AddAttribute(_seq++, "type", "button");
        b.AddAttribute(_seq++, "class", "ls-move-btn");
        b.AddAttribute(_seq++, "title", "上移（更靠前，更优先使用）");
        if (displayIndex <= 0)
            b.AddAttribute(_seq++, "disabled", true);
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, () => MoveGroup(displayIndex, -1)));
        b.AddEventStopPropagationAttribute(_seq++, "onclick", true);
        b.AddContent(_seq++, "↑");
        b.CloseElement();

        b.OpenElement(_seq++, "button");
        b.AddAttribute(_seq++, "type", "button");
        b.AddAttribute(_seq++, "class", "ls-move-btn");
        b.AddAttribute(_seq++, "title", "下移（更靠后）");
        if (displayIndex >= groupCount - 1)
            b.AddAttribute(_seq++, "disabled", true);
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, () => MoveGroup(displayIndex, 1)));
        b.AddEventStopPropagationAttribute(_seq++, "onclick", true);
        b.AddContent(_seq++, "↓");
        b.CloseElement();

        // 删除（仅备用组）：放在头部，避免再单独占一行
        if (isPrimary == false)
        {
            b.OpenElement(_seq++, "button");
            b.AddAttribute(_seq++, "type", "button");
            b.AddAttribute(_seq++, "class", "ls-btn-remove");
            b.AddAttribute(_seq++, "style", "padding:1px 9px; font-size:11px;");
            b.AddAttribute(_seq++, "title", "删除这一组");
            b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, () =>
            {
                if (channel != null)
                    _groupOpenStates.Remove(channel);
                Configuration!.RemoveGroup(slot);
                EnsureDetectArrays();
                StateHasChanged();
            }));
            b.AddEventStopPropagationAttribute(_seq++, "onclick", true);
            b.AddContent(_seq++, "删除");
            b.CloseElement();
        }

        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", groupOpen ? "ls-chevron ls-chevron-open" : "ls-chevron");
        b.AddContent(_seq++, "▶");
        b.CloseElement();
        b.CloseElement();

        // ── 副信息行：折叠时也能一眼判断这组能否用 ──
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-card-meta");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", configured ? "ls-dot ls-dot-ok" : "ls-dot ls-dot-off");
        b.CloseElement();
        b.AddContent(_seq++, configured ? "已就绪" : "未配置");
        if (string.IsNullOrWhiteSpace(modelId) == false)
        {
            b.OpenElement(_seq++, "code");
            b.AddContent(_seq++, modelId);
            b.CloseElement();
        }
        if (string.IsNullOrWhiteSpace(host) == false)
            b.AddContent(_seq++, "· " + host);
        b.CloseElement();

        // ── 展开内容 ──
        if (groupOpen)
        {
            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "class", "ls-details-content");
            GroupConfig(b, slot, isPrimary);
            b.CloseElement();
        }

        b.CloseElement();
    }

    /// <summary>拖放：交换两组的显示顺序（拖到最上方即成为主组）</summary>
    void DropGroup(int targetDisplayIndex)
    {
        if (_dragSource == null || _dragSource.Value == targetDisplayIndex)
        {
            _dragSource = null;
            return;
        }

        int src = _dragSource.Value;
        _dragSource = null;
        SwapGroups(src, targetDisplayIndex);
        EnsureDetectArrays();
        StateHasChanged();
    }

    /// <summary>按钮排序：把指定显示位置的组往前（delta=-1）或往后（delta=+1）移动一位</summary>
    void MoveGroup(int displayIndex, int delta)
    {
        int target = displayIndex + delta;
        var groups = Configuration!.Groups;
        if (displayIndex < 0 || displayIndex >= groups.Count || target < 0 || target >= groups.Count)
            return;

        SwapGroups(displayIndex, target);
        EnsureDetectArrays();
        StateHasChanged();
    }

    /// <summary>交换两个显示位置的渠道组（拖动与上下移动按钮共用），并让强制锁定索引跟随原组移动</summary>
    void SwapGroups(int src, int target)
    {
        var groups = Configuration!.Groups;
        (groups[src], groups[target]) = (groups[target], groups[src]);

        // 探测结果跟随原组移动，避免排序后"第 N 组"的探测结果与实际组错位
        if (src >= 0 && target >= 0 && src < _detectResults.Length && target < _detectResults.Length)
            (_detectResults[src], _detectResults[target]) = (_detectResults[target], _detectResults[src]);
        if (src >= 0 && target >= 0 && src < _detectedModels.Length && target < _detectedModels.Length)
            (_detectedModels[src], _detectedModels[target]) = (_detectedModels[target], _detectedModels[src]);

        // 排序后强制锁定跟随原组移动
        int forced = Configuration.ForcedGroupIndex;
        if (forced == src)
            Configuration.ForcedGroupIndex = target;
        else if (forced == target)
            Configuration.ForcedGroupIndex = src;
    }

    /// <summary>显示位置 → Groups 索引（显示位置即索引，列表顺序即顺序）</summary>
    int GetDisplaySlot(int displayIndex)
    {
        var order = LanguageModelRouter.GetGroupOrder(Configuration);
        if (displayIndex < 0 || displayIndex >= order.Length) return displayIndex;
        return order[displayIndex];
    }

    /// <summary>槽位是否为当前生效渠道（强制锁定优先，否则主组）</summary>
    bool IsCurrentSlot(int slot)
    {
        var cfg = Configuration!;
        if (cfg.ForcedGroupIndex >= 0) return cfg.ForcedGroupIndex == slot;
        return LanguageModelRouter.GetPrimarySlot(cfg) == slot;
    }

    // ==================== Probe & Dropdown ====================

    void ProbeSection(RenderTreeBuilder b, int groupIndex)
    {
        EnsureDetectArrays();
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-probe");

        b.OpenElement(_seq++, "button");
        b.AddAttribute(_seq++, "type", "button");
        b.AddAttribute(_seq++, "class", "ls-btn-probe");
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, async () =>
        {
            await ProbeGroup(groupIndex);
        }));
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-btn-probe-icon");
        b.AddContent(_seq++, "✧");
        b.CloseElement();
        b.AddContent(_seq++, "探测可用模型");
        b.CloseElement();

        var result = _detectResults[groupIndex];
        if (result != null)
        {
            b.OpenElement(_seq++, "span");
            bool ok = result.Contains("连接成功") || result.Contains("个模型");
            b.AddAttribute(_seq++, "class", ok ? "ls-probe-result ls-probe-ok" : "ls-probe-result ls-probe-fail");
            b.AddContent(_seq++, result);
            b.CloseElement();
        }

        var models = _detectedModels[groupIndex];
        if (models != null && models.Count > 0)
        {
            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "class", "ls-probe-select-row");

            b.OpenElement(_seq++, "span");
            b.AddContent(_seq++, "选择模型：");
            b.CloseElement();

            b.OpenElement(_seq++, "select");
            b.AddAttribute(_seq++, "class", "ls-select");
            b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
            {
                if (e.Value is string val && !string.IsNullOrWhiteSpace(val)
                    && groupIndex >= 0 && groupIndex < Configuration!.Groups.Count)
                {
                    Configuration.Groups[groupIndex].ModelId = val;
                    StateHasChanged();
                }
            }));
            b.OpenElement(_seq++, "option");
            b.AddAttribute(_seq++, "value", "");
            Configuration!.EnsureGroups();
            string cur = (groupIndex >= 0 && groupIndex < Configuration.Groups.Count)
                ? Configuration.Groups[groupIndex].ModelId ?? ""
                : "";
            b.AddContent(_seq++, $"— 当前: {(string.IsNullOrWhiteSpace(cur) ? "未设置" : cur)} —");
            b.CloseElement();
            foreach (var m in models)
            {
                b.OpenElement(_seq++, "option");
                b.AddAttribute(_seq++, "value", m);
                b.AddContent(_seq++, m);
                b.CloseElement();
            }
            b.CloseElement();

            b.CloseElement();
        }

        b.CloseElement();
    }

    async Task ProbeGroup(int groupIndex)
    {
        EnsureDetectArrays();
        if (groupIndex < 0 || groupIndex >= _detectResults.Length) return;

        _detectResults[groupIndex] = "探测中…";
        _detectedModels[groupIndex] = null;
        StateHasChanged();

        var (display, models) = await LanguageModelRouter.FetchModels(groupIndex, Configuration!);
        _detectResults[groupIndex] = display;
        _detectedModels[groupIndex] = models;
        StateHasChanged();
    }

    // ==================== API Key 加密开关 ====================

    void AddEncryptionToggle(RenderTreeBuilder b)
    {
        CompactSwitch(b, "encryptKeyCheck", Configuration.EncryptApiKeys,
            v =>
            {
                Configuration.EncryptApiKeys = v;
                // 立即按新开关状态统一转换所有 Key（开启→密文，关闭→明文），随配置保存生效
                Configuration.EnsureApiKeysProtected();
            },
            "加密保存 API Key",
            "用 Windows 系统加密，只有当前用户能解密；关闭则明文保存，便于迁移");
    }

    void AddSslToggle(RenderTreeBuilder b)
    {
        CompactSwitch(b, "ignoreSslCheck", Configuration.IgnoreSslCertificate,
            v => Configuration.IgnoreSslCertificate = v,
            "跳过证书校验",
            "兼容自建 / 自签名证书渠道；公用渠道建议关闭（更安全）");
    }

    // ==================== 多模态 ====================

    void AddMultimodalSection(RenderTreeBuilder b)
    {
        CompactSwitch(b, "persistentMediaCheck", Configuration.EnablePersistentMedia,
            v => Configuration.EnablePersistentMedia = v,
            "允许图片常驻上下文",
            "开启后 AI 可用 persistent=true 让这张图在之后多轮对话里依然可见（直到该段对话被记忆压缩归档）");

        Fold(b, "multimodal-adv", "多模态进阶（失败自愈 / 冷却 / 诊断）", () =>
        {
            CompactSwitch(b, "mediaRetryCheck", Configuration.MediaRetryWithoutMedia,
                v => Configuration.MediaRetryWithoutMedia = v,
                "被上游拒绝时去掉媒体重试",
                "同一组内自动剥离媒体再试一次，保证这一轮仍有回复，不弹错误窗");

            AddInput(b, "媒体冷却时长（分钟）", Configuration.MediaCooldownMinutes.ToString(), v =>
            {
                if (int.TryParse(v, out int n))
                    Configuration.MediaCooldownMinutes = Math.Clamp(n, 0, 1440);
            });
            Note(b, "某组拒绝过媒体后，这段时间内不再往该组发图片，避免反复踩坑；0 = 不冷却");

            AddInput(b, "单个媒体上限（MB）", Configuration.MediaMaxMegabytes.ToString(), v =>
            {
                if (int.TryParse(v, out int n))
                    Configuration.MediaMaxMegabytes = Math.Clamp(n, 1, 200);
            });
            Note(b, "超过上限直接拒绝发送，避免请求体过大被上游 413 拒绝");

            CompactSwitch(b, "debugPayloadCheck", Configuration.DebugLogPayload,
                v => Configuration.DebugLogPayload = v,
                "调试日志（请求摘要）",
                "在 alife.log 记录每次请求的媒体摘要与上游原始错误体，排查看图问题用；平时建议关闭");
        });
    }

    // ==================== 自定义命令 ====================

    void AddCommandSection(RenderTreeBuilder b)
    {
        CompactSwitch(b, "customCommandCheck", Configuration.EnableCustomCommands,
            v => Configuration.EnableCustomCommands = v,
            "启用自定义命令",
            "输入 /重新生成、/删除最新一轮对话 可直接操作上下文，不经过模型、不耗 token（默认关闭）");

        Fold(b, "command-adv", "命令列表与主人 QQ 校验", () =>
        {
            AddInput(b, "主人 QQ（QQ 环境只认这个号）", Configuration.OwnerQq ?? "",
                v => Configuration.OwnerQq = (v ?? "").Trim());
            Note(b, "QQ 群聊里一轮可能包含多人的消息，只有这条命令属于主人 QQ 时才会执行；未填写则 QQ 环境一律不执行");

            CompactSwitch(b, "requireOwnerQqCheck", Configuration.RequireOwnerQq,
                v => Configuration.RequireOwnerQq = v,
                "群聊只允许主人触发",
                "关闭后任何人在群里发命令都会执行（不建议）；桌面环境不受此限制");

            CompactSwitch(b, "commandEchoCheck", Configuration.CommandEcho,
                v => Configuration.CommandEcho = v,
                "命令执行后发一条提示",
                "桌面显示「✅已重新生成 / ✅已删除最新一轮对话」气泡；QQ 环境由插件直接发一条消息（私聊发私聊、群聊发当前群）。仅提示，不进入上下文");

            Configuration.EnsureCommandRules();
            for (int i = 0; i < Configuration.CommandRules.Count; i++)
                CommandRuleRow(b, i);

            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "class", "ls-add-row");
            b.OpenElement(_seq++, "button");
            b.AddAttribute(_seq++, "type", "button");
            b.AddAttribute(_seq++, "class", "ls-btn-add");
            b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, () =>
            {
                Configuration.CommandRules.Add(new CommandRule
                {
                    Trigger = "/新命令",
                    Action = CommandAction.Regenerate,
                    Enabled = true
                });
                StateHasChanged();
            }));
            b.AddContent(_seq++, "+ 添加命令");
            b.CloseElement();
            b.CloseElement();
        });
    }

    void CommandRuleRow(RenderTreeBuilder b, int index)
    {
        if (index < 0 || index >= Configuration.CommandRules.Count)
            return;

        CommandRule rule = Configuration.CommandRules[index];

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "style", "display:flex;gap:6px;align-items:center;margin:6px 0;flex-wrap:wrap;");

        b.OpenElement(_seq++, "input");
        b.AddAttribute(_seq++, "type", "checkbox");
        b.AddAttribute(_seq++, "checked", rule.Enabled);
        b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            rule.Enabled = e.Value is bool value && value;
            StateHasChanged();
        }));
        b.CloseElement();

        b.OpenComponent<Input<string>>(_seq++);
        b.AddAttribute(_seq++, "Value", rule.Trigger ?? "");
        b.AddAttribute(_seq++, "Style", "flex:1;min-width:150px;");
        b.AddAttribute(_seq++, "ValueChanged", EventCallback.Factory.Create<string>(this, v => rule.Trigger = v ?? ""));
        b.CloseComponent();

        AddSwitchBtn(b, "重新生成", rule.Action == CommandAction.Regenerate, true, () =>
        {
            rule.Action = CommandAction.Regenerate;
            StateHasChanged();
        });
        AddSwitchBtn(b, "删除上一轮", rule.Action == CommandAction.DeleteLastRound, true, () =>
        {
            rule.Action = CommandAction.DeleteLastRound;
            StateHasChanged();
        });
        AddSwitchBtn(b, "✕", false, true, () =>
        {
            Configuration.CommandRules.RemoveAt(index);
            StateHasChanged();
        });

        b.CloseElement();
    }

    // ==================== Auto Failover Toggle ====================

    void AutoFailoverToggle(RenderTreeBuilder b)
    {
        CompactSwitch(b, "autoFailoverCheck", Configuration.AutoFailoverEnabled,
            v => Configuration.AutoFailoverEnabled = v,
            "自动容灾",
            "遇到限流、额度用完、超时、网络不通时，自动换下一组渠道");

        CompactSwitch(b, "priorityMainCheck", Configuration.PriorityMainChannel,
            v => Configuration.PriorityMainChannel = v,
            "优先主渠道",
            "每次对话都先试主渠道，切换过程不打扰你（仅日志可见）");

        CompactSwitch(b, "showThinkingCheck", Configuration.ShowThinkingChain,
            v => Configuration.ShowThinkingChain = v,
            "显示思考过程",
            "在对话的「思考过程」区显示模型思考；关闭只影响显示，仍计入统计");
    }

    /// <summary>思考相关的低频微调（折叠）：忽略历史隐式功能占用 + 自定义思考触发关键词。</summary>
    void AddThinkingAdvanced(RenderTreeBuilder b)
    {
        Fold(b, "thinking-adv", "思考模式进阶", () =>
        {
            CompactSwitch(b, "ignorePersistentThinkingCheck", Configuration.IgnorePersistentThinking,
                v => Configuration.IgnorePersistentThinking = v,
                "忽略历史里的待思考标记",
                "历史中调用过工具也照常走非思考，日常闲聊更省 token；真要调工具时仍会自动思考");

            AddInput(b, "关键词强制思考（逗号分隔）", Configuration.ThinkingTriggerKeywords ?? "",
                v => Configuration.ThinkingTriggerKeywords = string.IsNullOrWhiteSpace(v) ? null : v);
            Note(b, "消息命中这些词就临时走思考，例如：代码,数学,分析");
        });
    }

    /// <summary>容灾低频参数（折叠）：错误关键字、重试间隔、请求超时、流空闲超时。</summary>
    void AddDisasterAdvanced(RenderTreeBuilder b)
    {
        Fold(b, "disaster", "更多容灾参数", () =>
        {
            AddInput(b, "额外错误关键字（逗号分隔）", Configuration.ErrorKeywords ?? "",
                v => Configuration.ErrorKeywords = string.IsNullOrEmpty(v) ? null : v);
            Note(b, "返回内容含这些词就换组（如 rate_limit,quota）。限流、额度不足、内容审查已内置，一般留空");

            AddInput(b, "重试间隔（毫秒）", Configuration.RetryDelayMs.ToString(), v =>
            {
                if (int.TryParse(v, out var n))
                    Configuration.RetryDelayMs = Math.Clamp(n, 0, 30000);
            });
            Note(b, "换组前等待时间，默认 1000ms；0 = 立即重试");

            AddInput(b, "请求超时（毫秒）", Configuration.RequestTimeoutMs.ToString(), v =>
            {
                if (int.TryParse(v, out var n))
                    Configuration.RequestTimeoutMs = Math.Clamp(n, 1000, 300000);
            });
            Note(b, "单次渠道请求超时，默认 30000ms；渠道卡住时按此时间放弃并换组");

            AddInput(b, "流空闲超时（毫秒）", Configuration.StreamIdleTimeoutMs.ToString(), v =>
            {
                if (int.TryParse(v, out var n))
                    Configuration.StreamIdleTimeoutMs = Math.Clamp(n, 0, 600000);
            });
            Note(b, "连上后长时间没数据就报错，默认 0 = 关闭；思考很久的模型别设太小");
        });
    }

    // ==================== Switch ====================

    void SwitchTo(int groupIndex)
    {
        // -1 = 自动容灾；其他索引必须指向已配置组，否则不生效（避免 UI 显示锁定但实际走主组的假象）
        if (groupIndex >= 0 && !IsGroupConfigured(groupIndex))
        {
            // 提示并保持现状
            return;
        }
        Configuration.ForcedGroupIndex = groupIndex;
        // 立即把面板里的配置同步给运行中的模块并落盘：手动切换无需再点「保存配置」即生效、重启后仍保留
        Module?.ApplyConfigAndSave(Configuration);
        LanguageModelRouter.OnGroupChanged?.Invoke();
        StateHasChanged();
    }

    void AddSwitchBtn(RenderTreeBuilder b, string text, bool active, bool configured, Action onClick)
    {
        b.OpenElement(_seq++, "button");
        b.AddAttribute(_seq++, "type", "button");
        string cls = active ? "ls-btn ls-btn-active" : "ls-btn";
        if (!configured && !active) cls += " ls-btn-dim";
        b.AddAttribute(_seq++, "class", cls);
        b.AddAttribute(_seq++, "onclick", EventCallback.Factory.Create(this, onClick));
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    // ==================== Shared Helpers ====================

    string GetActiveGroupLabel()
    {
        int idx = Configuration.ForcedGroupIndex;
        if (idx < 0) return "自动容灾（主组优先）";
        if (idx >= Configuration.Groups.Count || !Configuration.Groups[idx].IsConfigured)
            return "自动容灾（当前锁定组未配置）";
        return LanguageModelRouter.GetGroupLabel(idx, Configuration);
    }


    /// <summary>采样温度显示值：null 显示为空（=不发送该参数）；用不变文化格式化，避免小数分隔符随系统区域变化</summary>
    static string FormatTemperature(double? value)
        => value.HasValue ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>解析采样温度输入：留空或非法返回 null（=不发送该参数，使用服务端默认）</summary>
    static double? ParseTemperature(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        return double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double t) ? t : null;
    }

    void AddLabel(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-label");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    void AddInput(RenderTreeBuilder b, string label, string value, Action<string> setter)
    {
        AddLabel(b, label);
        b.OpenComponent<Input<string>>(_seq++);
        b.AddAttribute(_seq++, "Value", value);
        b.AddAttribute(_seq++, "ValueChanged",
            EventCallback.Factory.Create<string>(this, setter));
        b.CloseComponent();
    }

    void AddPassword(RenderTreeBuilder b, string label, string value, Action<string> setter)
    {
        AddLabel(b, label);
        b.OpenComponent<InputPassword>(_seq++);
        b.AddAttribute(_seq++, "Value", value);
        b.AddAttribute(_seq++, "ValueChanged",
            EventCallback.Factory.Create<string>(this, setter));
        b.CloseComponent();
    }

}
