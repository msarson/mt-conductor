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
    hint.textContent = card.openHint;
    hint.hidden = !openHints.has(card.id);
    const openBtn = el.querySelector(".open");
    if (card.activeAgents.length === 0) {
      // Nothing we can point at yet: bringing a closed project up "where I left off" needs
      // MT's Open endpoint (phase 3, upstream PR).
      openBtn.disabled = true;
      openBtn.title = "Opening a project that isn't running needs MT's Open endpoint (coming in phase 3).";
      hint.hidden = true;
      return el;
    }
    openBtn.title = "Shows which agent is working on this project";
    openBtn.addEventListener("click", () => {
      if (openHints.has(card.id)) openHints.delete(card.id); else openHints.add(card.id);
      hint.hidden = !openHints.has(card.id);
    });

    return el;
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
    });
    host.postMessage({ type: "ready" });
  } else {
    statusEl.textContent = "Open this page from MT Conductor.";
  }
})();
