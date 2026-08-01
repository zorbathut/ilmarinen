// Client-rendered log viewer for /jobs/{id}/logs.
//
// The server streams stored NDJSON chunks and retains nothing, so this holds the window: a bounded
// run of chunks that slides in either direction as you scroll, re-fetching whatever it evicted.
//
// The entry decoding below must agree with LogChunkParser.cs, which decodes the same stored format
// on the server for the download and raw views.

const MAX_BYTES = 12 * 1024 * 1024; // rendered text held before the far end of the window is evicted
const MAX_LINE_CHARS = 10000;       // one line can be a whole minified bundle, which no line budget would catch
const PAGE_LINES = 2000;            // a fetch keeps going until it has this much, since a chunk is anywhere from one line to 4 KB
const FETCH_LIMIT = 200;            // the server's own page cap
const MAX_SEQUENCE = 2147483647;    // asks the feed for "nothing newer than everything": an empty body carrying only the status
const POLL_MS = 1000;
const TAIL_SLOP_PX = 40;            // how far off the bottom still counts as following the tail

const TERMINAL_STATUSES = ["Success", "Failed", "Cancelled"];

const views = new Map();
let nextHandle = 1;

export function attach(container, jobId, status) {
    if (container.dataset.logViewer) {
        return Number(container.dataset.logViewer);
    }

    const handle = nextHandle++;
    const view = new LogView(container, jobId, status);

    container.dataset.logViewer = String(handle);
    views.set(handle, view);
    view.start(); // Deliberately not awaited: the first fetch fills the pane, and nothing here waits on it.

    return handle;
}

export function detach(handle) {
    const view = views.get(handle);
    if (view) {
        view.stop();
        views.delete(handle);
    }
}

class LogView {
    constructor(container, jobId, status) {
        this.jobId = jobId;
        this.status = status;
        this.blocks = [];
        this.heldBytes = 0;
        this.heldLines = 0;
        this.atStart = false;   // the window reaches the first chunk of the log
        this.atEnd = true;      // the window reaches the newest chunk
        this.caughtUp = false;  // a forward fetch has come back empty
        this.following = true;
        this.autoScroll = true;
        this.busy = false;
        this.pendingBackfill = false;
        this.generation = 0;
        this.stopped = false;
        this.pollTimer = null;

        this.build(container);
    }

    build(container) {
        this.toolbar = el("div", "log-toolbar");

        checkbox(this.toolbar, "log-autoscroll", "Auto-scroll", true, (on) => {
            this.autoScroll = on;
            if (on && !this.following) {
                this.jumpToTail();
            }
        });
        checkbox(this.toolbar, "log-stderr", "Show stderr", true, (on) => {
            this.pane.classList.toggle("hide-stderr", !on);
        });

        this.tailButton = el("button", "log-tail-button");
        this.tailButton.type = "button";
        this.tailButton.textContent = "Jump to tail";
        this.tailButton.addEventListener("click", () => this.jumpToTail());

        this.stats = el("span", "log-stats");
        this.statusLabel = el("span", "log-status");
        this.toolbar.appendChild(this.stats);
        this.toolbar.appendChild(this.tailButton);
        this.toolbar.appendChild(this.statusLabel);

        this.pane = el("div", "logfull-pane");
        this.pane.tabIndex = 0;
        // Deliberately no aria-live: announcing a streaming log of this size would be unusable.
        this.pane.addEventListener("scroll", () => this.onScroll());

        container.appendChild(this.toolbar);
        container.appendChild(this.pane);

        this.paint();
    }

    async start() {
        await this.jumpToTail();
        this.schedulePoll();
    }

    stop() {
        this.stopped = true;
        if (this.pollTimer) {
            clearTimeout(this.pollTimer);
            this.pollTimer = null;
        }
    }

    get firstSeq() {
        return this.blocks.length > 0 ? this.blocks[0].firstSeq : null;
    }

    get lastSeq() {
        return this.blocks.length > 0 ? this.blocks[this.blocks.length - 1].lastSeq : null;
    }

    /**
     * Reading history and following the tail are separate modes. Appending while the user has
     * scrolled up would evict the very lines they are reading, and walking forward from far back
     * would chase the whole remainder of the log a page at a time — so coming back to the tail
     * throws the window away and re-fetches it instead.
     */
    async jumpToTail() {
        this.generation++;
        this.clear();
        this.following = true;
        this.atEnd = true;
        this.caughtUp = false;

        await this.load(null);

        this.pane.scrollTop = this.pane.scrollHeight;
        this.schedulePoll();
    }

    async backfill() {
        if (this.atStart) {
            return;
        }

        if (this.busy) {
            // A scroll that lands while a fetch is in flight would otherwise be dropped, and since the view doesn't move there would be no later scroll event to retry it.
            this.pendingBackfill = true;
            return;
        }

        await this.load(this.firstSeq);

        if (this.pendingBackfill) {
            this.pendingBackfill = false;
            await this.backfill();
        }
    }

    /**
     * Pulls older chunks until PAGE_LINES have arrived or the log runs out. A null bound means the
     * tail. Returns the number of lines added.
     */
    async load(before) {
        if (this.busy || this.stopped) {
            return 0;
        }

        this.busy = true;
        let added = 0;
        let bound = before;

        try {
            while (added < PAGE_LINES && !this.stopped) {
                const query = bound === null ? "" : `before=${bound}&`;
                const page = await this.fetchWindow(`?${query}limit=${FETCH_LIMIT}`);

                if (page === null) {
                    break;
                }

                if (page.firstSeq === undefined) {
                    // An empty response means there is nothing earlier — but only when we asked for something earlier.
                    this.atStart = bound !== null;
                    break;
                }

                if (!this.insert(page, true)) {
                    break;
                }

                added += page.lines;
                bound = page.firstSeq;

                if (bound <= 1) {
                    this.atStart = true;
                    break;
                }
            }
        } finally {
            this.busy = false;
        }

        this.paint();
        return added;
    }

    /** Appends anything newer than the window. Returns the number of lines added. */
    async poll() {
        if (this.busy || this.stopped) {
            return 0;
        }

        this.busy = true;

        try {
            const page = await this.fetchWindow(`?after=${this.lastSeq ?? 0}&limit=${FETCH_LIMIT}`);

            if (page === null) {
                return 0;
            }

            if (page.firstSeq === undefined) {
                this.caughtUp = true;
                this.atEnd = true;
                return 0;
            }

            this.caughtUp = false;
            return this.insert(page, false) ? page.lines : 0;
        } finally {
            this.busy = false;
            this.paint();
        }
    }

    /** Reads the job's status without transferring any log content. */
    async probeStatus() {
        await this.fetchWindow(`?after=${MAX_SEQUENCE}&limit=1`);
        this.paint();
    }

    /**
     * One request. Returns null when it failed, a block with a sequence range when it carried
     * content, or a marker with no firstSeq when the window was empty — which is not the same thing
     * as a page whose chunks happened to render no lines.
     */
    async fetchWindow(query) {
        try {
            const response = await fetch(`/api/jobs/${this.jobId}/logs/chunks${query}`);
            if (!response.ok) {
                this.showError(`the log feed returned ${response.status}`);
                return null;
            }

            this.clearError();
            this.setStatus(response.headers.get("X-Job-Status"));

            const body = await response.text();
            if (body.length === 0) {
                return { lines: 0 };
            }

            const block = this.renderBlock(body);
            block.firstSeq = Number(response.headers.get("X-Log-First-Sequence"));
            block.lastSeq = Number(response.headers.get("X-Log-Last-Sequence"));
            return block;
        } catch (error) {
            this.showError(`could not reach the log feed: ${error}`);
            return null;
        }
    }

    /** Returns false when the window was reset while this page was in flight. */
    insert(block, prepend) {
        if (block.generation !== this.generation) {
            return false;
        }

        if (prepend) {
            this.pane.insertBefore(block.el, this.pane.firstChild);
            this.blocks.unshift(block);

            // Hold the reader's place against exactly what appeared above the viewport. Measuring the pane's net height change instead would come out near zero whenever eviction below the viewport offsets the prepend — leaving the view pinned at the top, where it stops producing the scroll events that drive further backfill.
            this.pane.scrollTop += block.el.offsetHeight;
        } else {
            this.pane.appendChild(block.el);
            this.blocks.push(block);
        }

        this.heldBytes += block.bytes;
        this.heldLines += block.lines;

        // Evict from the end being moved away from, so the window stays a contiguous run of chunks — which is what makes re-fetching either edge a single request.
        while (this.heldBytes > MAX_BYTES && this.blocks.length > 1) {
            const dropped = prepend ? this.blocks.pop() : this.blocks.shift();
            const droppedHeight = dropped.el.offsetHeight;

            dropped.el.remove();
            this.heldBytes -= dropped.bytes;
            this.heldLines -= dropped.lines;

            if (prepend) {
                // The newest chunks are no longer loaded, so the tail is somewhere below the window now.
                this.atEnd = false;
                this.caughtUp = false;
            } else {
                this.atStart = false;
                this.pane.scrollTop -= droppedHeight;
            }
        }

        return true;
    }

    /**
     * Decodes one response into a single element. stdout accumulates into text nodes and only
     * stderr and metadata become elements, so a mostly-stdout log stays a handful of DOM nodes and
     * the browser's own find and select-all keep working across the window.
     */
    renderBlock(body) {
        const block = el("div", "log-block");
        let lines = 0;
        let bytes = 0;
        let pending = "";

        const flush = () => {
            if (pending.length > 0) {
                block.appendChild(document.createTextNode(pending));
                pending = "";
            }
        };

        for (const raw of body.split("\n")) {
            if (raw.length === 0) {
                continue;
            }

            let entry;
            try {
                entry = JSON.parse(raw);
            } catch {
                continue; // A line that will not parse means corrupt storage; dropping it beats failing the whole view.
            }

            if (typeof entry.d !== "string" || !["o", "e", "m"].includes(entry.t)) {
                continue;
            }

            for (const line of entry.d.split("\n")) {
                if (line.length === 0) {
                    continue;
                }

                const text = line.length > MAX_LINE_CHARS
                    ? `${line.slice(0, MAX_LINE_CHARS)} …(line truncated — use Raw or Download)`
                    : line;

                if (entry.t === "o") {
                    pending += `${text}\n`;
                } else {
                    flush();
                    const span = el("span", entry.t === "e" ? "log-stderr" : "log-meta");
                    // The newline goes inside the span so hiding stderr doesn't leave a blank line behind for every hidden one.
                    span.textContent = `${text}\n`;
                    block.appendChild(span);
                }

                bytes += text.length + 1;
                lines++;
            }
        }

        flush();
        return { el: block, lines, bytes, generation: this.generation, firstSeq: undefined, lastSeq: undefined };
    }

    onScroll() {
        const distanceFromBottom = this.pane.scrollHeight - this.pane.scrollTop - this.pane.clientHeight;

        // Only the end of the log counts as following; the bottom of a window whose tail was evicted is just more history.
        this.following = this.atEnd && distanceFromBottom <= TAIL_SLOP_PX;

        if (this.pane.scrollTop <= TAIL_SLOP_PX) {
            this.backfill();
        }

        this.schedulePoll();
        this.paint();
    }

    schedulePoll() {
        if (this.stopped || this.pollTimer || this.finished()) {
            return;
        }

        this.pollTimer = setTimeout(async () => {
            this.pollTimer = null;
            await this.tick();
            this.schedulePoll();
        }, POLL_MS);
    }

    /**
     * A finished job stops changing, so polling continues only while there is still tail left to
     * drain — and resumes if the reader comes back to the tail to look for it.
     */
    finished() {
        if (!TERMINAL_STATUSES.includes(this.status)) {
            return false;
        }

        return this.caughtUp || !this.following;
    }

    async tick() {
        if (!this.following) {
            // Nothing to append while the user reads history, but the status still has to catch the job finishing — that is also what stops the polling.
            await this.probeStatus();
            return;
        }

        const added = await this.poll();
        if (added > 0 && this.autoScroll) {
            this.pane.scrollTop = this.pane.scrollHeight;
        }
    }

    setStatus(status) {
        if (status && status !== this.status) {
            this.status = status;
            this.paint();
        }
    }

    clear() {
        this.pane.replaceChildren();
        this.blocks = [];
        this.heldBytes = 0;
        this.heldLines = 0;
        this.atStart = false;
    }

    /** A failed fetch is usually transient, so it warns and lets the next poll try again. */
    showError(message) {
        if (!this.errorBanner) {
            this.errorBanner = el("div", "log-error");
            this.toolbar.insertAdjacentElement("afterend", this.errorBanner);
        }

        this.errorBanner.textContent = `${message} — retrying`;
    }

    clearError() {
        if (this.errorBanner) {
            this.errorBanner.remove();
            this.errorBanner = null;
        }
    }

    paint() {
        let held;
        if (this.heldLines === 0) {
            held = TERMINAL_STATUSES.includes(this.status) ? "No logs" : "No logs yet";
        } else if (this.atStart) {
            held = `${this.heldLines.toLocaleString()} lines`;
        } else {
            held = `${this.heldLines.toLocaleString()} lines (scroll up for earlier)`;
        }

        this.stats.textContent = `${held} | ${formatBytes(this.heldBytes)}`;
        this.tailButton.hidden = this.following;
        this.statusLabel.textContent = this.status;
        this.statusLabel.className = `log-status ${TERMINAL_STATUSES.includes(this.status) ? "complete" : "streaming"}`;
    }
}

function el(tag, className) {
    const node = document.createElement(tag);
    node.className = className;
    return node;
}

function checkbox(parent, id, text, checked, onChange) {
    const label = el("label", "log-option");
    const input = document.createElement("input");

    input.type = "checkbox";
    input.id = id;
    input.checked = checked;
    input.addEventListener("change", () => onChange(input.checked));

    label.htmlFor = id;
    label.appendChild(input);
    label.appendChild(document.createTextNode(` ${text}`));
    parent.appendChild(label);

    return input;
}

function formatBytes(bytes) {
    if (bytes < 1024) {
        return `${bytes} B`;
    }
    if (bytes < 1024 * 1024) {
        return `${(bytes / 1024).toFixed(1)} KB`;
    }
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}
