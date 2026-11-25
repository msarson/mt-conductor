// MT Conductor page. Receives {type:"view", view} messages from the host and renders one card
// per project. All data is inserted with textContent, never as HTML.
(() => {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  const cardsEl = document.getElementById("cards");
  const template = document.getElementById("card-template");
  const statusEl = document.getElementById("status");
  const remoteEl = document.getElementById("remote");
  const offlineEl = document.getElementById("offline");
  const emptyEl = document.getElementById("empty");
  const unmatchedEl = document.getElementById("unmatched");

  // Which cards have their Open hint expanded, by project id, so refreshes don't collapse them.
  const openHints = new Set();
  let hasCards = false;
  let lastView = null;

  const CHECK_GLYPH = { success: "✓", failure: "✗", pending: "…", none: "" };

  function plural(n, one, many) {
    return `${n} ${n === 1 ? one : many}`;
  }

  function timeText(iso) {
    const d = new Date(iso);
    return isNaN(d) ? "" : d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  }

  function renderPrs(container, card) {
    container.replaceChildren();
    container.title = "";
    if (card.pullRequests === null || card.pullRequests === undefined) {
      container.textContent = card.hasGitHubRepo ? "PRs: unknown" : "PRs: no GitHub repo found";
      container.title = card.hasGitHubRepo
        ? "Couldn't read PRs: gh missing or not logged in, or the repo isn't on GitHub."
        : "No gitRepoUrl in MT and no upstream or origin remote in the project folder.";
      return;
    }
    const others = card.otherPullRequests > 0 ? ` +${card.otherPullRequests} by others` : "";
    if (card.pullRequests.length === 0) {
      container.textContent = others ? `PRs: none of yours open ·${others}` : "PRs: none open";
      return;
    }
    container.append("PRs: ");
    for (const pr of card.pullRequests) {
      const a = document.createElement("a");
      a.href = pr.url;
      a.title = [pr.title, pr.isDraft ? "draft" : null, pr.review ? pr.review.toLowerCase().replaceAll("_", " ") : null, `checks: ${pr.checks}`]
        .filter(Boolean).join(" · ");
      a.textContent = `#${pr.number}`;
      const glyph = CHECK_GLYPH[pr.checks] || "";
      if (glyph) {
        const s = document.createElement("span");
        s.className = `check-${pr.checks}`;
        s.textContent = ` ${glyph}`;
        a.append(s);
      }
      if (pr.isDraft) a.append(" (draft)");
      a.addEventListener("click", (e) => {
        e.preventDefault();
        if (host) host.postMessage({ type: "openUrl", url: pr.url });
      });
      container.append(a);
    }
    if (others) container.append(others.trim());
  }

  function renderCard(card) {
    const el = template.content.firstElementChild.cloneNode(true);
    el.dataset.id = card.id;
    el.classList.toggle("needs-you", card.needsYou);
    el.classList.toggle("parked", card.status === "Parked");

    el.querySelector(".name").textContent = card.name;
    el.querySelector(".pm").textContent = card.pm ? `(PM: ${card.pm})` : "";
    if (card.status === "Parked") el.querySelector(".pm").textContent += " · parked";

    el.querySelector(".needs").hidden = !card.needsYou;
    const reasons = el.querySelector(".reasons");
    reasons.hidden = card.needsYouReasons.length === 0;
    for (const r of card.needsYouReasons) {
      const li = document.createElement("li");
      li.textContent = r;
      reasons.append(li);
    }

    const leftoff = el.querySelector(".leftoff");
    if (card.leftOff) {
      const age = document.createElement("span");
      age.className = "age";
      age.textContent = card.leftOffAge ? `Left off ${card.leftOffAge}: ` : "Left off: ";
      leftoff.append(age, card.leftOff);
    } else {
      leftoff.textContent = "Left off: nothing recorded yet";
      leftoff.classList.add("age");
    }

    el.querySelector(".tasks").textContent =
      `Tasks: ${card.inProgress} in progress · ${card.toDo} to do · ${card.awaitingTest} ready for me to test`;

    renderPrs(el.querySelector(".prs"), card);
    const questions = el.querySelector(".questions");
    questions.textContent = `Questions: ${card.questions}`;
    questions.title = "Escalations plus permission requests/questions from the last 24 hours. "
      + "MT only records the latter while remote mode is on, so at the desk check the project's panes too.";

    const hint = el.querySelector(".open-hint");
    const openBtn = el.querySelector(".open");

    if (openSupported !== false) {
      renderOpen(card, openBtn, hint);
      return el;
    }

    // This MT has no Open endpoint (older than ClarionLive/multiterminal#103): say where the
    // project is, if an agent has it open, and nothing more.
    hint.textContent = card.openHint;
    hint.hidden = !openHints.has(card.id);
    if (card.activeAgents.length === 0) {
      openBtn.disabled = true;
      openBtn.title = OPEN_UNSUPPORTED;
      hint.hidden = true;
      return el;
    }
    openBtn.title = `Shows which agent is working on this project. ${OPEN_UNSUPPORTED}`;
    openBtn.addEventListener("click", () => {
      if (openHints.has(card.id)) openHints.delete(card.id); else openHints.add(card.id);
      hint.hidden = !openHints.has(card.id);
    });

    return el;
  }

  // ---- Open (MT's POST /api/projects/{id}/open, sent by the host) ----

  const OPEN_UNSUPPORTED = "Opening from here needs a newer MT (ClarionLive/multiterminal#103).";
  const LAUNCH_TIMEOUT_MS = 90_000;
  const NOTE_MS = 10_000;
  let openSupported = null;         // from the view; null = not tried yet
  // projectId -> { pending, launchingSince, text, until }
  const openState = new Map();

  function renderOpen(card, openBtn, hint) {
    const now = Date.now();
    let st = openState.get(card.id);

    // A launch is done once an agent shows up on the project, or gives up after the timeout.
    if (st && st.launchingSince) {
      if (card.activeAgents.length > 0) {
        st = { text: `Open: ${card.activeAgents.join(", ")}`, until: now + NOTE_MS };
        openState.set(card.id, st);
      } else if (now - st.launchingSince > LAUNCH_TIMEOUT_MS) {
        // We only see agents that have an active task here, so this is "can't tell", not "failed".
        st = { text: "Couldn't confirm it opened. Check MT: it may be asking how to start, or the launch was cancelled.", until: now + NOTE_MS * 3 };
        openState.set(card.id, st);
      }
    }
    if (st && st.until && now > st.until) {
      openState.delete(card.id);
      st = undefined;
    }

    openBtn.disabled = !!(st && (st.pending || st.launchingSince));
    openBtn.title = card.activeAgents.length > 0
      ? "Bring this project's PM pane to the front in MT"
      : "Open this project in MT where you left off";
    hint.textContent = st ? st.text : "";
    hint.hidden = !st;

    openBtn.addEventListener("click", () => {
      if (!host) return;
      openState.set(card.id, { pending: true, text: "Asking MT…" });
      openBtn.disabled = true;
      hint.textContent = "Asking MT…";
      hint.hidden = false;
      host.postMessage({ type: "openProject", projectId: card.id });
    });
  }

  function onOpenResult(msg) {
    const now = Date.now();
    const note = (text, ms = NOTE_MS) => ({ text, until: now + ms });
    let st;
    switch (msg.kind) {
      case "focused": {
        let text = `Switched to ${msg.paneTitle || "its pane"}`;
        if (msg.floating) text += " (floating window)";
        if (msg.otherPanes > 0) text += ` · ${plural(msg.otherPanes, "other pane", "other panes")} also have it open`;
        st = note(text);
        break;
      }
      case "launching":
        st = { launchingSince: now, text: "Opening in MT… answer MT if it asks how to start." };
        break;
      case "notfound":
        st = note(`MT doesn't know this project. ${msg.detail || ""}`.trim(), NOTE_MS * 2);
        break;
      case "unsupported":
        openSupported = false;
        st = note(OPEN_UNSUPPORTED, NOTE_MS * 2);
        break;
      case "offline":
        st = note("MT isn't answering.");
        break;
      default:
        st = note(`MT couldn't open it. ${msg.detail || ""}`.trim(), NOTE_MS * 2);
    }
    openState.set(msg.projectId, st);
    if (lastView) render(lastView);
  }

  function render(view) {
    offlineEl.hidden = view.mtOnline;
    cardsEl.classList.toggle("stale", !view.mtOnline);
    statusEl.className = `pill ${view.mtOnline ? "online" : "offline"}`;
    const warnings = (view.mtOnline && view.warnings) ? view.warnings : [];
    statusEl.textContent = view.mtOnline ? `MT online · updated ${timeText(view.updatedAt)}` : "MT offline";
    if (warnings.length > 0) {
      statusEl.className = "pill offline";
      statusEl.textContent += ` · ${warnings.join(", ")}`;
    }

    if (view.remoteMode === null || view.remoteMode === undefined) {
      remoteEl.hidden = true;
    } else {
      remoteEl.hidden = false;
      const changed = view.remoteModeChangedAt ? new Date(view.remoteModeChangedAt) : null;
      const since = changed && !isNaN(changed)
        ? ` · since ${changed.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`
        : "";
      remoteEl.textContent = `Remote mode: ${view.remoteMode ? "on" : "off"}${since}`;
      remoteEl.title = "MT's remote mode is one switch for all projects. When it's on, agents' questions go "
        + "to your phone for a few minutes before falling back to their panes. MT turns it on and off itself "
        + "(idle, desk activity, phone).";
    }

    // Offline: keep the last cards on screen, dimmed.
    if (!view.mtOnline) return;

    lastView = view;
    // The host's knowledge wins, except that an "unsupported" answer we just got sticks until
    // the host has seen it too (it resets when MT restarts).
    if (view.openSupported !== null && view.openSupported !== undefined) openSupported = view.openSupported;
    else if (openSupported !== false) openSupported = null;

    cardsEl.replaceChildren(...view.cards.map(renderCard));
    hasCards = view.cards.length > 0;
    emptyEl.hidden = hasCards;

    unmatchedEl.hidden = view.unmatchedQuestions === 0;
    unmatchedEl.textContent = view.unmatchedQuestions === 0 ? ""
      : `${plural(view.unmatchedQuestions, "question or escalation", "questions or escalations")} couldn't be matched to a project.`;
  }

  if (host) {
    host.addEventListener("message", (e) => {
      if (e.data && e.data.type === "view") render(e.data.view);
      else if (e.data && e.data.type === "openResult") onOpenResult(e.data);
    });
    host.postMessage({ type: "ready" });
  } else {
    statusEl.textContent = "Open this page from MT Conductor.";
  }
})();
