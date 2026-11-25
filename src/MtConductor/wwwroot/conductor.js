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
  const laterNoteEl = document.getElementById("later-note");

  // Which cards have their Open hint expanded, by project id, so refreshes don't collapse them.
  const openHints = new Set();
  let hasCards = false;
  let lastView = null;      // last online view (what the cards show)
  let currentView = null;   // last view of any kind

  const CHECK_GLYPH = { success: "✓", failure: "✗", pending: "…", none: "" };

  function plural(n, one, many) {
    return `${n} ${n === 1 ? one : many}`;
  }

  function timeText(iso, noSeconds = false) {
    const d = new Date(iso);
    if (isNaN(d)) return "";
    return d.toLocaleTimeString([], noSeconds
      ? { hour: "2-digit", minute: "2-digit" }
      : { hour: "2-digit", minute: "2-digit", second: "2-digit" });
  }

  // Gives an element a hover list (one line per entry) and marks it as having one.
  function withTip(el, lines) {
    if (lines.length === 0) return;
    el.title = lines.join("\n");
    el.classList.add("tip");
  }

  // Where the PR numbers come from and whose "yours" is, so a surprising list can be traced.
  function prSource(card) {
    if (!card.prRepo) return "";
    return card.githubLogin
      ? `PRs from ${card.prRepo}, yours = ${card.githubLogin} (gh login)`
      : `PRs from ${card.prRepo}. gh login unknown, so they aren't split into yours and others'.`;
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
    container.title = prSource(card);
    // When the general list failed, only your own PRs are known: say so rather than imply no others.
    const others = card.othersKnown === false ? " +? by others"
      : card.otherPullRequests > 0 ? ` +${card.otherPullRequests} by others` : "";
    if (card.pullRequests.length === 0) {
      container.textContent = card.othersKnown === false ? "PRs: none of yours open · others unknown"
        : others ? `PRs: none of yours open ·${others}` : "PRs: none open";
      return;
    }
    // Without gh's login the list can't be split into yours and others', so say only how many are open.
    container.append(card.prOwnerKnown === false ? `PRs: ${card.pullRequests.length} open · ` : "PRs: ");
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
    card.needsYouReasons.forEach((r, i) => {
      const li = document.createElement("li");
      li.textContent = r;
      withTip(li, card.reasonDetails && card.reasonDetails[i] ? [card.reasonDetails[i]] : []);
      reasons.append(li);
    });

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

    // Each count explains itself on hover: the titles of the tasks behind it.
    const tasks = el.querySelector(".tasks");
    const counts = [
      [`${card.inProgress} in progress`, card.inProgressTitles],
      [`${card.toDo} to do`, card.toDoTitles],
      [`${card.awaitingTest} ready for me to test`, card.awaitingTestTip],
    ];
    tasks.append("Tasks: ");
    counts.forEach(([text, titles], i) => {
      if (i > 0) tasks.append(" · ");
      const span = document.createElement("span");
      span.textContent = text;
      withTip(span, titles || []);
      tasks.append(span);
    });

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
    const note = liveNote(card.id);   // e.g. the "needs a newer MT" answer to the click just made
    if (note) {
      hint.textContent = note;
      hint.hidden = false;
    }
    if (card.activeAgents.length === 0) {
      openBtn.disabled = true;
      openBtn.title = OPEN_UNSUPPORTED;
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
  const LAUNCH_WATCH_MS = 90_000;   // how long to watch for a new agent after a launch
  const PENDING_MS = 15_000;        // the host always answers; this is only a backstop
  // MT takes the foreground when Open works, so the Owner reads the card's note only after
  // switching back: keep notes up for a minute (a new click replaces them at once).
  const NOTE_MS = 60_000;
  const NOANSWER_COOLDOWN_MS = 30_000;
  let openSupported = null;         // from the host's view; null = not tried yet
  // projectId -> { text, pendingSince?, launchingSince?, agentsBefore?, until?, busyUntil? }
  const openState = new Map();

  // A note that hasn't expired yet, or null (and forget expired ones).
  function liveNote(id) {
    const st = openState.get(id);
    if (!st) return null;
    if (st.until && Date.now() > st.until) {
      openState.delete(id);
      return null;
    }
    return st.text;
  }

  function showHint(hint, text) {
    hint.textContent = text || "";
    hint.hidden = !text;
  }

  function renderOpen(card, openBtn, hint) {
    const now = Date.now();
    let st = openState.get(card.id);

    if (st && st.pendingSince && now - st.pendingSince > PENDING_MS) {
      st = { text: "MT didn't answer.", until: now + NOTE_MS };
      openState.set(card.id, st);
    }
    // After a launch, only an agent that wasn't there before counts as "it opened". A new PM
    // with no active task can't be seen from here, so running out of time claims nothing.
    if (st && st.launchingSince) {
      const fresh = card.activeAgents.filter((a) => !st.agentsBefore.includes(a));
      if (fresh.length > 0) {
        st = { text: `Open: ${fresh.join(", ")}`, until: now + NOTE_MS };
        openState.set(card.id, st);
      } else if (now - st.launchingSince > LAUNCH_WATCH_MS) {
        st = { text: "Launch requested in MT. If nothing appeared, MT may be asking how to start.", until: now + NOTE_MS * 2 };
        openState.set(card.id, st);
      }
    }
    const text = liveNote(card.id);
    st = openState.get(card.id);

    openBtn.disabled = !!(st && (st.pendingSince || st.launchingSince || (st.busyUntil && now < st.busyUntil)));
    openBtn.title = card.activeAgents.length > 0
      ? "Open in MT: brings up the pane that has this project open"
      : "Open in MT: starts this project the way its launch setting says (new, continue or ask)";
    showHint(hint, text);

    openBtn.addEventListener("click", () => {
      if (!host) return;
      openState.set(card.id, { pendingSince: Date.now(), text: "Asking MT…" });
      openBtn.disabled = true;
      showHint(hint, "Asking MT…");
      host.postMessage({ type: "openProject", projectId: card.id });
    });
  }

  function onOpenResult(msg) {
    const now = Date.now();
    const note = (text, ms = NOTE_MS) => ({ text, until: now + ms });
    const card = lastView && lastView.cards.find((c) => c.id === msg.projectId);
    let st;
    switch (msg.kind) {
      case "focused": {
        let text = `Asked MT to bring up ${msg.paneTitle || "its pane"}`;
        if (msg.floating) text += " (floating window)";
        if (msg.otherPanes > 0) text += ` · ${plural(msg.otherPanes, "other pane", "other panes")} also have it open`;
        st = note(text);
        break;
      }
      case "launching":
        st = {
          launchingSince: now,
          agentsBefore: card ? card.activeAgents.slice() : [],
          text: "Starting in MT… answer MT if it asks how to start.",
        };
        break;
      case "notfound":
        st = note(`MT doesn't know this project. ${msg.detail || ""}`.trim(), NOTE_MS * 2);
        break;
      case "unsupported":
        openSupported = false;   // the host's next view says the same
        st = note(OPEN_UNSUPPORTED, NOTE_MS * 2);
        break;
      case "offline":
        st = note("MT isn't running or isn't reachable.");
        break;
      case "noanswer":
        // The open may still happen once MT's dialog closes: don't invite a second launch at once.
        st = { ...note("MT didn't answer in time. It may be showing a dialog; the open can still happen.", NOTE_MS * 2), busyUntil: now + NOANSWER_COOLDOWN_MS };
        break;
      default:
        st = note(`MT couldn't open it. ${msg.detail || ""}`.trim(), NOTE_MS * 2);
    }
    openState.set(msg.projectId, st);
    // While MT is offline the dimmed cards stay as they are; the note shows when it's back.
    if (lastView && currentView === lastView) render(lastView);
  }

  function render(view) {
    currentView = view;
    offlineEl.hidden = view.mtOnline;
    cardsEl.classList.toggle("stale", !view.mtOnline);
    statusEl.className = `pill ${view.mtOnline ? "online" : "offline"}`;
    const warnings = view.warnings || [];   // also while offline: a conductor error is ours, not MT's
    statusEl.textContent = view.mtOnline ? `MT online · updated ${timeText(view.updatedAt)}` : "MT offline";
    if (warnings.length > 0) {
      statusEl.className = "pill offline";
      statusEl.textContent += ` · ${warnings.join(", ")}`;
    }

    if (view.remoteMode === null || view.remoteMode === undefined) {
      remoteEl.hidden = true;
    } else {
      remoteEl.hidden = false;
      const changedText = view.remoteModeChangedAt ? timeText(view.remoteModeChangedAt, true) : "";
      const since = changedText ? ` · since ${changedText}` : "";
      remoteEl.textContent = `Remote mode: ${view.remoteMode ? "on" : "off"}${since}`;
      remoteEl.title = "MT's remote mode is one switch for all projects. When it's on, agents' questions go "
        + "to your phone for a few minutes before falling back to their panes. MT turns it on and off itself "
        + "(idle, desk activity, phone).";
    }

    // Offline: keep the last cards on screen, dimmed, with nothing to click through to.
    if (!view.mtOnline) {
      for (const b of cardsEl.querySelectorAll("button.open")) b.disabled = true;
      return;
    }

    // A new view from the host is the authority on Open support (it forgets when MT restarts).
    // Re-rendering the same view after an openResult keeps what that result just taught us.
    if (view !== lastView) {
      openSupported = view.openSupported ?? null;
      const ids = new Set(view.cards.map((c) => c.id));
      for (const id of openState.keys()) if (!ids.has(id)) openState.delete(id);
    }
    lastView = view;

    cardsEl.replaceChildren(...view.cards.map(renderCard));
    hasCards = view.cards.length > 0;
    emptyEl.hidden = hasCards;
    laterNoteEl.hidden = !hasCards;   // explains the two greyed-out buttons once, not on every card

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
