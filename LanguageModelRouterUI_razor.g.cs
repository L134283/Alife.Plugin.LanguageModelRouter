using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using AntDesign;

namespace Alife.Plugin.LanguageModelRouter;

public partial class LanguageModelRouterUI : ModuleUIBase<LanguageModelRouter, LanguageModelRouterConfig>
{
    string?[] _detectResults = new string?[4];
    List<string>?[] _detectedModels = new List<string>?[4];
    int _seq;

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
";


    protected override void OnInitialized()
    {
        LanguageModelRouter.OnGroupChanged = () => InvokeAsync(StateHasChanged);
        if (Configuration != null)
        {
            LanguageModelRouter.PriorityMainChannel = Configuration.PriorityMainChannel;
            LanguageModelRouter.ShowThinkingChain = Configuration.ShowThinkingChain;
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
        b.AddContent(_seq++, "灵枢 · OpenAI语言模型报错自动切换");
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-title-sub");
        b.AddContent(_seq++, "—  通  道  ·  容  灾  ·  思  维  —");
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-title-bar");
        b.CloseElement();
        b.CloseElement();

        // 特性条（装饰，非业务文案）
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-feature-strip");
        FeatureChip(b, "四路圣渠");
        FeatureChip(b, "自动容灾");
        FeatureChip(b, "思维链");
        FeatureChip(b, "一语切换");
        FeatureChip(b, "模型探测");
        b.CloseElement();

        // 说明（圣谕碑）
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-alert");

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-alert-header");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-alert-header-icon");
        b.AddContent(_seq++, "✦");
        b.CloseElement();
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-alert-header-title");
        b.AddContent(_seq++, "灵枢圣谕");
        b.CloseElement();
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-alert-header-line");
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-alert-body");
        AlertLine(b, "✧", "替换框架内置的 OpenAILanguageModel，实现多路文本模型自动容灾切换", false);
        AlertLine(b, "✧", "遇到 HTTP 429/402/5xx 错误或响应体包含指定关键字时，自动切换到下一组渠道重试", false);
        AlertLine(b, "✧", "同时支持 reasoning_content 等 SSE 思维链流的自动转换", false);
        AlertLine(b, "◈", "使用前请在角色配置中禁用 OpenAILanguageModel，启用本模块", true);
        b.CloseElement();

        b.CloseElement();

        // === 第1组 ===
        GroupConfig(b, 0, true);

        // 圣印分隔
        SealDivider(b);

        // === 第2组 ===
        AddCollapsibleGroup(b, "备用渠道 1（第2组）", () => GroupConfig(b, 1, false));

        // === 第3组 ===
        AddCollapsibleGroup(b, "备用渠道 2（第3组）", () => GroupConfig(b, 2, false));

        // === 第4组 ===
        AddCollapsibleGroup(b, "备用渠道 3（第4组）", () => GroupConfig(b, 3, false));

        SealDivider(b);

        // === 容灾设置 ===
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-section");
        SectionTitle(b, "容灾设置");

        AutoFailoverToggle(b);

        AddInput(b, "错误关键字（逗号分隔）", Configuration.ErrorKeywords ?? "", v => Configuration.ErrorKeywords = string.IsNullOrEmpty(v) ? null : v);
        AddHint(b, "响应体中包含这些关键字时触发切换，如 rate_limit,insufficient_quota,billing_hard_limit\n留空则仅按 HTTP 状态码判断");

        AddInput(b, "重试间隔（毫秒）", Configuration.RetryDelayMs.ToString(), v =>
        {
            if (int.TryParse(v, out var n))
                Configuration.RetryDelayMs = Math.Clamp(n, 0, 30000);
        });
        AddHint(b, "切换到下一组前的等待时间，默认 1000ms，设为 0 则立即重试");

        b.CloseElement();

        // === 手动切换 ===
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-section");
        SectionTitle(b, "手动切换");

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-status-row");
        b.AddContent(_seq++, $"当前状态：{GetActiveGroupLabel()}");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", LanguageModelRouter.ForcedGroupIndex < 0 ? "ls-badge ls-badge-on" : "ls-badge ls-badge-active");
        b.AddContent(_seq++, LanguageModelRouter.ForcedGroupIndex < 0 ? "自动容灾" : "强制锁定");
        b.CloseElement();
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-btn-row");

        int forcedIdx = LanguageModelRouter.ForcedGroupIndex;
        for (int g = 0; g < 4; g++)
        {
            int group = g;
            string name = LanguageModelRouter.GetGroupName(g, Configuration);
            string btnLabel = string.IsNullOrWhiteSpace(name) ? $"第{group + 1}组" : $"第{group + 1}组({name})";
            bool configured = IsGroupConfigured(group);
            AddSwitchBtn(b, btnLabel, forcedIdx == group, configured, () => SwitchTo(group));
        }

        b.CloseElement();
        AddHint(b, "点击按钮切换渠道，也可在聊天中告诉桌宠「切换到第二组」或按名称「切换到 deepseek」，AI 会自动切换。配置保存后即刻生效，无需重新加载模块。");
        b.CloseElement();

        // === 使用说明 ===
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-section");
        SectionTitle(b, "使用说明");
        AddHint(b, "1. 在角色配置中禁用「OpenAI语言模型」，启用「灵枢 - OpenAI语言模型报错自动切换」\n2. 第1组为主渠道，必须填写 Endpoint、Model ID 和 API Key\n3. 第2~4组为备用渠道，遇到 429/402/5xx 错误时自动切换\n4. 组名称可用于 AI 识别渠道，如对桌宠说「切换到 deepseek」即可对应切换\n5. 配置保存后即刻生效，无需重新加载模块");
        b.CloseElement();
    }

    void FeatureChip(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-feature-chip");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    void AlertLine(RenderTreeBuilder b, string mark, string text, bool warn)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", warn ? "ls-alert-line ls-alert-line-warn" : "ls-alert-line");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-alert-mark");
        b.AddContent(_seq++, mark);
        b.CloseElement();
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-alert-text");
        b.AddContent(_seq++, text);
        b.CloseElement();
        b.CloseElement();
    }

    void SealDivider(RenderTreeBuilder b)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-seal");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-seal-icon");
        b.AddContent(_seq++, "✦");
        b.CloseElement();
        b.CloseElement();
    }

    bool IsGroupConfigured(int g)
    {
        var cfg = Configuration!;
        return g switch
        {
            0 => !string.IsNullOrWhiteSpace(cfg.Endpoint1) && !string.IsNullOrWhiteSpace(cfg.ApiKey1),
            1 => !string.IsNullOrWhiteSpace(cfg.Endpoint2) && !string.IsNullOrWhiteSpace(cfg.ApiKey2),
            2 => !string.IsNullOrWhiteSpace(cfg.Endpoint3) && !string.IsNullOrWhiteSpace(cfg.ApiKey3),
            3 => !string.IsNullOrWhiteSpace(cfg.Endpoint4) && !string.IsNullOrWhiteSpace(cfg.ApiKey4),
            _ => false
        };
    }

    // ==================== Group Config ====================

    void GroupConfig(RenderTreeBuilder b, int g, bool isFirst)
    {
        if (isFirst)
        {
            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "class", "ls-section");
            SectionTitle(b, "主渠道（第1组，必填）");

            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "class", "ls-status-row");
            b.OpenElement(_seq++, "span");
            b.AddAttribute(_seq++, "class", IsGroupConfigured(0) ? "ls-badge ls-badge-on" : "ls-badge ls-badge-off");
            b.AddContent(_seq++, IsGroupConfigured(0) ? "✦ 已配置" : "○ 未配置");
            b.CloseElement();
            if (LanguageModelRouter.ForcedGroupIndex == 0 || LanguageModelRouter.ForcedGroupIndex < 0)
            {
                b.OpenElement(_seq++, "span");
                b.AddAttribute(_seq++, "class", "ls-badge ls-badge-active");
                b.AddContent(_seq++, "当前通道");
                b.CloseElement();
            }
            b.CloseElement();
        }

        var cfg = Configuration!;

        string groupName = g switch
        {
            0 => cfg.GroupName1 ?? "",
            1 => cfg.GroupName2 ?? "",
            2 => cfg.GroupName3 ?? "",
            3 => cfg.GroupName4 ?? "",
            _ => ""
        };
        string endpoint = g switch
        {
            0 => cfg.Endpoint1,
            1 => cfg.Endpoint2 ?? "",
            2 => cfg.Endpoint3 ?? "",
            3 => cfg.Endpoint4 ?? "",
            _ => ""
        };
        string modelId = g switch
        {
            0 => cfg.ModelId1,
            1 => cfg.ModelId2 ?? "",
            2 => cfg.ModelId3 ?? "",
            3 => cfg.ModelId4 ?? "",
            _ => ""
        };
        string apiKey = g switch
        {
            0 => cfg.ApiKey1,
            1 => cfg.ApiKey2 ?? "",
            2 => cfg.ApiKey3 ?? "",
            3 => cfg.ApiKey4 ?? "",
            _ => ""
        };
        string reasoning = g switch
        {
            0 => cfg.ReasoningEffort1 ?? "",
            1 => cfg.ReasoningEffort2 ?? "",
            2 => cfg.ReasoningEffort3 ?? "",
            3 => cfg.ReasoningEffort4 ?? "",
            _ => ""
        };
        string extraH = g switch
        {
            0 => cfg.ExtraHeaders1 ?? "",
            1 => cfg.ExtraHeaders2 ?? "",
            2 => cfg.ExtraHeaders3 ?? "",
            3 => cfg.ExtraHeaders4 ?? "",
            _ => ""
        };
        string extraB = g switch
        {
            0 => cfg.ExtraBody1 ?? "",
            1 => cfg.ExtraBody2 ?? "",
            2 => cfg.ExtraBody3 ?? "",
            3 => cfg.ExtraBody4 ?? "",
            _ => ""
        };

        if (!isFirst)
        {
            b.OpenElement(_seq++, "div");
            b.AddAttribute(_seq++, "class", "ls-status-row");
            b.OpenElement(_seq++, "span");
            b.AddAttribute(_seq++, "class", IsGroupConfigured(g) ? "ls-badge ls-badge-on" : "ls-badge ls-badge-off");
            b.AddContent(_seq++, IsGroupConfigured(g) ? "✦ 已配置" : "○ 未配置");
            b.CloseElement();
            if (LanguageModelRouter.ForcedGroupIndex == g)
            {
                b.OpenElement(_seq++, "span");
                b.AddAttribute(_seq++, "class", "ls-badge ls-badge-active");
                b.AddContent(_seq++, "当前通道");
                b.CloseElement();
            }
            b.CloseElement();
        }

        AddInput(b, "组名称（可选，供 AI 识别）", groupName, v => SetGroupName(g, v));
        AddInput(b, "Endpoint", endpoint, v => SetGroupEndpoint(g, v));
        AddHint(b, "API 端点 URL，如 https://api.openai.com/v1");
        AddInput(b, "Model ID", modelId, v => SetGroupModelId(g, v));
        AddHint(b, "模型标识，如 gpt-4o、deepseek-chat");

        ProbeSection(b, g);

        AddPassword(b, "API Key", apiKey, v => SetGroupApiKey(g, v));

        AddInput(b, "Reasoning Effort", reasoning, v => SetGroupReasoning(g, v));
        AddHint(b, "推理强度，如 low / medium / high，留空则不设置");
        AddInput(b, "Extra Headers (JSON)", extraH, v => SetGroupExtraHeaders(g, v));
        AddHint(b, "额外请求头，JSON 格式，如 {\"X-Custom\":\"value\"}");
        AddInput(b, "Extra Body (JSON)", extraB, v => SetGroupExtraBody(g, v));
        AddHint(b, "额外请求体，JSON 格式，如 {\"temperature\":0.7}");

        if (isFirst)
            b.CloseElement();
    }

    void SetGroupName(int g, string v)
    {
        if (g == 0) Configuration!.GroupName1 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 1) Configuration!.GroupName2 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 2) Configuration!.GroupName3 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 3) Configuration!.GroupName4 = string.IsNullOrWhiteSpace(v) ? null : v;
    }

    void SetGroupEndpoint(int g, string v)
    {
        if (g == 0) Configuration!.Endpoint1 = v;
        else if (g == 1) Configuration!.Endpoint2 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 2) Configuration!.Endpoint3 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 3) Configuration!.Endpoint4 = string.IsNullOrWhiteSpace(v) ? null : v;
    }

    void SetGroupModelId(int g, string v)
    {
        if (g == 0) Configuration!.ModelId1 = v;
        else if (g == 1) Configuration!.ModelId2 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 2) Configuration!.ModelId3 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 3) Configuration!.ModelId4 = string.IsNullOrWhiteSpace(v) ? null : v;
    }

    void SetGroupApiKey(int g, string v)
    {
        if (g == 0) Configuration!.ApiKey1 = v;
        else if (g == 1) Configuration!.ApiKey2 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 2) Configuration!.ApiKey3 = string.IsNullOrWhiteSpace(v) ? null : v;
        else if (g == 3) Configuration!.ApiKey4 = string.IsNullOrWhiteSpace(v) ? null : v;
    }

    void SetGroupReasoning(int g, string v)
    {
        string? val = string.IsNullOrWhiteSpace(v) ? null : v;
        if (g == 0) Configuration!.ReasoningEffort1 = val;
        else if (g == 1) Configuration!.ReasoningEffort2 = val;
        else if (g == 2) Configuration!.ReasoningEffort3 = val;
        else if (g == 3) Configuration!.ReasoningEffort4 = val;
    }

    void SetGroupExtraHeaders(int g, string v)
    {
        string? val = string.IsNullOrWhiteSpace(v) ? null : v;
        if (g == 0) Configuration!.ExtraHeaders1 = val;
        else if (g == 1) Configuration!.ExtraHeaders2 = val;
        else if (g == 2) Configuration!.ExtraHeaders3 = val;
        else if (g == 3) Configuration!.ExtraHeaders4 = val;
    }

    void SetGroupExtraBody(int g, string v)
    {
        string? val = string.IsNullOrWhiteSpace(v) ? null : v;
        if (g == 0) Configuration!.ExtraBody1 = val;
        else if (g == 1) Configuration!.ExtraBody2 = val;
        else if (g == 2) Configuration!.ExtraBody3 = val;
        else if (g == 3) Configuration!.ExtraBody4 = val;
    }

    // ==================== Probe & Dropdown ====================

    void ProbeSection(RenderTreeBuilder b, int groupIndex)
    {
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
        b.AddContent(_seq++, $"探测第{groupIndex + 1}组");
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
                if (e.Value is string val && !string.IsNullOrWhiteSpace(val))
                {
                    SetGroupModelId(groupIndex, val);
                    StateHasChanged();
                }
            }));
            b.OpenElement(_seq++, "option");
            b.AddAttribute(_seq++, "value", "");
            string cur = groupIndex switch
            {
                0 => Configuration!.ModelId1,
                1 => Configuration!.ModelId2,
                2 => Configuration!.ModelId3,
                _ => Configuration!.ModelId4,
            };
            b.AddContent(_seq++, $"— 当前: {cur ?? "未设置"} —");
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
        _detectResults[groupIndex] = "探测中…";
        _detectedModels[groupIndex] = null;
        StateHasChanged();

        var (display, models) = await LanguageModelRouter.FetchModels(groupIndex, Configuration!);
        _detectResults[groupIndex] = display;
        _detectedModels[groupIndex] = models;
        StateHasChanged();
    }

    // ==================== Auto Failover Toggle ====================

    void AutoFailoverToggle(RenderTreeBuilder b)
    {
        // 自动容灾
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-toggle-row");

        b.OpenElement(_seq++, "input");
        b.AddAttribute(_seq++, "type", "checkbox");
        b.AddAttribute(_seq++, "id", "autoFailoverCheck");
        b.AddAttribute(_seq++, "checked", LanguageModelRouter.AutoFailoverEnabled);
        b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            LanguageModelRouter.AutoFailoverEnabled = e.Value is bool bv ? bv : !LanguageModelRouter.AutoFailoverEnabled;
            StateHasChanged();
        }));
        b.CloseElement();

        b.OpenElement(_seq++, "label");
        b.AddAttribute(_seq++, "for", "autoFailoverCheck");
        b.AddContent(_seq++, "启用自动容灾（开启后无论切换到哪个组，遇错误时自动切换备用渠道）");
        b.CloseElement();
        b.CloseElement();

        // 优先主渠道
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-toggle-row");

        b.OpenElement(_seq++, "input");
        b.AddAttribute(_seq++, "type", "checkbox");
        b.AddAttribute(_seq++, "id", "priorityMainCheck");
        b.AddAttribute(_seq++, "checked", LanguageModelRouter.PriorityMainChannel);
        b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            LanguageModelRouter.PriorityMainChannel = e.Value is bool bv ? bv : !LanguageModelRouter.PriorityMainChannel;
            Configuration!.PriorityMainChannel = LanguageModelRouter.PriorityMainChannel;
            StateHasChanged();
        }));
        b.CloseElement();

        b.OpenElement(_seq++, "label");
        b.AddAttribute(_seq++, "for", "priorityMainCheck");
        b.AddContent(_seq++, "优先主渠道（每次对话优先尝试主渠道，容灾全程静默，仅日志可见）");
        b.CloseElement();
        b.CloseElement();

        // 思维链显示
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-toggle-row");

        b.OpenElement(_seq++, "input");
        b.AddAttribute(_seq++, "type", "checkbox");
        b.AddAttribute(_seq++, "id", "showThinkingCheck");
        b.AddAttribute(_seq++, "checked", LanguageModelRouter.ShowThinkingChain);
        b.AddAttribute(_seq++, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e =>
        {
            LanguageModelRouter.ShowThinkingChain = e.Value is bool bv ? bv : !LanguageModelRouter.ShowThinkingChain;
            Configuration!.ShowThinkingChain = LanguageModelRouter.ShowThinkingChain;
            StateHasChanged();
        }));
        b.CloseElement();

        b.OpenElement(_seq++, "label");
        b.AddAttribute(_seq++, "for", "showThinkingCheck");
        b.AddContent(_seq++, "显示思维链（开启后将 reasoning/thinking 等字段转为可见内容，关闭则隐藏；实时生效）");
        b.CloseElement();
        b.CloseElement();
    }

    // ==================== Switch ====================

    void SwitchTo(int groupIndex)
    {
        LanguageModelRouter.ForcedGroupIndex = groupIndex;
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
        int idx = LanguageModelRouter.ForcedGroupIndex;
        if (idx < 0) return "自动容灾";
        return LanguageModelRouter.GetGroupLabel(idx, Configuration);
    }

    void SectionTitle(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-section-title");
        b.AddContent(_seq++, text);
        b.CloseElement();
    }

    void AddHint(RenderTreeBuilder b, string text)
    {
        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-hint");
        b.AddContent(_seq++, text);
        b.CloseElement();
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

    void AddCollapsibleGroup(RenderTreeBuilder b, string title, Action renderContent)
    {
        b.OpenElement(_seq++, "details");
        b.AddAttribute(_seq++, "class", "ls-details");

        b.OpenElement(_seq++, "summary");
        b.OpenElement(_seq++, "span");
        b.AddAttribute(_seq++, "class", "ls-arrow");
        b.AddContent(_seq++, "▶");
        b.CloseElement();
        b.AddContent(_seq++, " " + title);
        b.CloseElement();

        b.OpenElement(_seq++, "div");
        b.AddAttribute(_seq++, "class", "ls-details-content");
        renderContent();
        b.CloseElement();
        b.CloseElement();
    }
}
