/**
 * StardewPilot pi extension.
 *
 * Connects to the bridge mod inside the running game, registers the bridge's tools as pi tools,
 * switches off every non-Stardew tool, and replaces pi's coding system prompt.
 * It also carries the in-game chat: player messages from the overlay become user messages,
 * proactive tip triggers are worded by the model, and replies go back to the game.
 *
 * Run from the repo root:  pi --no-builtin-tools -e ./agent/src/index.ts
 */
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";
import { BridgeClient, type McpTool, openWebSocket, readConnectionInfo, type Transport } from "./bridge-client.ts";
import { FIRST_YEAR_CHECKLIST } from "./first-year-checklist.ts";
import { buildSystemPrompt } from "./system-prompt.ts";

const STATUS_KEY = "stardewpilot";
const ACTION_TIMEOUT_MS = 20 * 60 * 1000;
/** Failed actions allowed per run: the first try plus two re-plans. */
export const MAX_FAILED_ACTIONS = 3;

/** Opens the transport to the bridge. Replaced in tests. */
export type Connector = () => Promise<Transport>;

const defaultConnector: Connector = () => openWebSocket(readConnectionInfo());

export default function stardewPilot(pi: ExtensionAPI) {
	createStardewPilot(pi, defaultConnector);
}

export function createStardewPilot(pi: ExtensionAPI, connector: Connector) {
	let client: BridgeClient | undefined;
	let connecting: Promise<BridgeClient | undefined> | undefined;
	let lastError = "";
	const registered = new Set<string>();
	let bridgeTools: McpTool[] = [];
	let currentCtx: ExtensionContext | undefined;
	let settings: BridgeSettings = {};
	let running = false;
	let runKind: "reply" | "tip" | "briefing" = "reply";
	const queuedTips: TipTrigger[] = [];
	const readOnly = new Set<string>();
	let failedActions = 0;
	let selfPlay = false;
	let actionsThisRun = 0;
	let idleRuns = 0;
	let newDayPending = false;
	let stepTimer: ReturnType<typeof setTimeout> | undefined;
	let reconnectTimer: ReturnType<typeof setInterval> | undefined;
	let active = false;

	/** Keep trying to reach the game in the background, so in-game chat works after a game restart. */
	function startReconnecting() {
		if (reconnectTimer || !active) return;
		reconnectTimer = setInterval(async () => {
			if (client && !client.isClosed) {
				clearInterval(reconnectTimer);
				reconnectTimer = undefined;
				return;
			}
			await ensureConnected();
		}, 5000);
		reconnectTimer.unref?.();
	}

	function stopReconnecting() {
		if (reconnectTimer) clearInterval(reconnectTimer);
		reconnectTimer = undefined;
	}

	function setStatus(text: string) {
		try {
			currentCtx?.ui.setStatus(STATUS_KEY, text);
		} catch {
			// The session ended or was replaced; its ctx can no longer be used.
			currentCtx = undefined;
		}
	}

	/** Only Stardew tools may be active. */
	function lockDownTools() {
		pi.setActiveTools([...registered]);
	}

	function registerBridgeTools(tools: McpTool[]) {
		bridgeTools = tools;
		for (const tool of tools) {
			if (registered.has(tool.name)) continue;
			registered.add(tool.name);
			if (tool.annotations?.readOnlyHint) readOnly.add(tool.name);
			const isAction = !tool.annotations?.readOnlyHint;
			pi.registerTool({
				name: tool.name,
				label: tool.name,
				description: tool.description,
				parameters: tool.inputSchema as never,
				annotations: { readOnlyHint: tool.annotations?.readOnlyHint ?? false },
				// The farmer can only do one thing at a time: run actions one after another, never in parallel.
				executionMode: isAction ? "sequential" : undefined,
				async execute(_toolCallId, params, signal) {
					const c = await ensureConnected();
					if (!c) throw new Error(`Not connected to the game: ${lastError}`);
					// Escape in pi aborts the call; stop the job in the game too.
					const onAbort = () => void c.callTool("stop_autopilot", {}).catch(() => {});
					if (isAction) signal?.addEventListener("abort", onAbort, { once: true });
					try {
						const result = await c.callTool(tool.name, (params ?? {}) as Record<string, unknown>, isAction ? ACTION_TIMEOUT_MS : undefined);
						if (isGameAction(tool.name) && actionFailed(result)) failedActions++;
						return {
							content: result.content,
							details: undefined,
							isError: result.isError === true,
							// Going to bed ends the day: end this run so the next day starts in a fresh session.
							terminate: tool.name === "go_to_bed" && !actionFailed(result) ? true : undefined,
						};
					} finally {
						signal?.removeEventListener("abort", onAbort);
					}
				},
			});
		}
		lockDownTools();
	}

	async function connect(): Promise<BridgeClient | undefined> {
		try {
			const transport = await connector();
			const c = new BridgeClient(transport);
			transport.onClose(() => {
				if (client === c) client = undefined;
				setStatus("🌱 game disconnected");
				startReconnecting();
			});
			const init = await c.request<{ gameVersion?: string; settings?: BridgeSettings }>("initialize", {
				clientInfo: { name: "stardewpilot-pi", version: "0.1.0" },
			});
			settings = init.settings ?? {};
			selfPlay = settings.selfPlay === true;
			registerBridgeTools(await c.listTools());
			c.onGameEvent((ev) => {
				if (ev.type === "day_started") {
					setStatus("🌱 connected · new day");
					// Each game day gets a fresh agent session, seeded from memory.
					newDayPending = true;
					if (!running) startNewDay();
					else
						pi.sendMessage(
							{ customType: "stardew-day-ended", content: "[A new game day has started] Stop here and reply in one line; a fresh session continues the new day.", display: true },
							{ deliverAs: "steer" },
						);
				}
			});
			c.onNotification((method, params) => handleBridgeNotification(method, params));
			client = c;
			lastError = "";
			if (selfPlay && !running) scheduleSelfPlayStep(2000); // self-play was already on (new session, or reconnect)
			setStatus(`🌱 connected · game ${init.gameVersion ?? "?"}`);
			return c;
		} catch (err) {
			lastError = err instanceof Error ? err.message : String(err);
			setStatus("🌱 game not connected");
			startReconnecting();
			return undefined;
		}
	}

	function ensureConnected(): Promise<BridgeClient | undefined> {
		if (client && !client.isClosed) return Promise.resolve(client);
		connecting ??= connect().finally(() => {
			connecting = undefined;
		});
		return connecting;
	}

	/** Messages the game pushes to the agent. */
	function handleBridgeNotification(method: string, params: unknown) {
		const p = (params ?? {}) as Record<string, unknown>;
		if (method === "chat/user_message" && typeof p.text === "string" && p.text.trim()) {
			// The player typed in the private in-game overlay.
			if (running) pi.sendUserMessage(p.text, { deliverAs: "followUp" });
			else {
				runKind = "reply";
				pi.sendUserMessage(p.text);
			}
		} else if (method === "tips/trigger") {
			queuedTips.push(p as unknown as TipTrigger);
			if (!running) startNextTip();
		} else if (method === "selfplay/changed") {
			const was = selfPlay;
			selfPlay = p.enabled === true;
			idleRuns = 0;
			if (selfPlay && !was && !running) {
				pi.sendMessage(
					{
						customType: "stardew-selfplay",
						content:
							"[Self-play started by the player] Call read_memory. If there is no plan for today, write one with write_day_plan (toward the goal, or the default first-year plan if there is none). If read_memory says selfPlay.followHabits, plan the way the player plays: their usual morning order first, what they usually do on a day like today, bed around their usual time (the goal still leads). Then start working on it.",
						display: true,
					},
					{ triggerTurn: true, deliverAs: "followUp" },
				);
			}
			if (!selfPlay) cancelStep();
		} else if (method === "routines/ran" && typeof p.text === "string") {
			// The mod ran one of the player's routines by its schedule and told the player itself; keep it as context.
			pi.sendMessage(
				{ customType: "stardew-routine", content: `[The mod ran the player's routine by its schedule] ${p.text}`, display: true },
				{ deliverAs: "nextTurn" },
			);
		} else if (method === "tips/warning" && typeof p.text === "string") {
			// Already shown to the player as fixed text; keep it as context for later answers.
			pi.sendMessage(
				{ customType: "stardew-warning", content: `[The game showed the player this warning] ${p.text}`, display: true },
				{ deliverAs: "nextTurn" },
			);
		}
	}

	function startNextTip() {
		const tip = queuedTips.shift();
		if (!tip) return;
		runKind = "tip";
		pi.sendMessage(
			{ customType: "stardew-tip", content: tipPrompt(tip, settings.gameLanguage), display: true },
			{ triggerTurn: true, deliverAs: "followUp" },
		);
	}

	/** Tools that make the farmer act (not reads, memory writes or self-play switches). */
	function isGameAction(name: string): boolean {
		// go_to_bed is a safety action (passing out costs gold): never blocked by the re-plan limit.
		return !readOnly.has(name) && !name.startsWith("write_") && !["set_self_play", "stop_autopilot", "go_to_bed", "save_routine", "forget_routine", "schedule_routine", "unschedule_routine"].includes(name);
	}

	function cancelStep() {
		if (stepTimer) clearTimeout(stepTimer);
		stepTimer = undefined;
	}

	/** Ask the game whether self-play may continue, then send the next step (or wait and ask again). */
	function scheduleSelfPlayStep(delayMs: number) {
		cancelStep();
		if (!selfPlay) return;
		stepTimer = setTimeout(async () => {
			stepTimer = undefined;
			if (running || !selfPlay || newDayPending) return;
			const c = await ensureConnected();
			if (!c) return scheduleSelfPlayStep(10_000);
			let next: { go?: boolean; wait?: boolean; waitSeconds?: number; brief?: string; reason?: string };
			try {
				next = await c.request("selfplay/next");
			} catch {
				return scheduleSelfPlayStep(10_000);
			}
			if (next.go) {
				pi.sendMessage(
					{
						customType: "stardew-selfplay",
						content: `[Self-play] ${next.brief ?? ""} Continue with today's plan: do the next task with the action tools. If the plan is done, pick something useful toward the goal, or write the day summary and go to bed if it is evening.`,
						display: true,
					},
					{ triggerTurn: true, deliverAs: "followUp" },
				);
			} else if (next.wait) {
				scheduleSelfPlayStep((next.waitSeconds ?? 3) * 1000);
			} else {
				selfPlay = false;
			}
		}, delayMs);
		stepTimer.unref?.();
	}

	/** Start a fresh pi session for the new game day (through a command, which may replace the session). */
	function startNewDay() {
		newDayPending = false;
		cancelStep();
		pi.sendUserMessage("/stardew-newday", { expandPromptTemplates: true });
	}

	pi.on("agent_start", async () => {
		cancelStep();
		actionsThisRun = 0;
		running = true;
		failedActions = 0;
		client?.notify("agent/status", { busy: true });
	});

	pi.on("tool_execution_start", async (event) => {
		if (isGameAction(event.toolName)) actionsThisRun++;
		client?.notify("agent/status", { busy: true, activity: describeTool(event.toolName) });
	});

	// Send each finished answer to the in-game chat, and count model use for the daily budget.
	pi.on("message_end", async (event) => {
		const message = event.message as { role?: string; content?: unknown; usage?: { totalTokens?: number } };
		if (message.role !== "assistant" || !Array.isArray(message.content)) return;
		client?.notify("agent/usage", { requests: 1, tokens: message.usage?.totalTokens ?? 0 });
		const parts = message.content as Array<{ type: string; text?: string }>;
		if (parts.some((part) => part.type === "toolCall")) return; // a step, not the answer
		const text = parts
			.filter((part) => part.type === "text" && part.text)
			.map((part) => part.text)
			.join("\n")
			.trim();
		if (text) client?.notify("chat/agent_message", { text, kind: runKind });
	});

	pi.on("agent_settled", async (event) => {
		running = false;
		runKind = "reply";
		client?.notify("agent/status", { busy: false });
		if (newDayPending) return startNewDay();
		if (queuedTips.length > 0) return startNextTip();
		if (!selfPlay) return;
		if ((event as { aborted?: boolean }).aborted) {
			// The player pressed Escape in pi: pause self-play.
			selfPlay = false;
			void client?.callTool("set_self_play", { enabled: false }).catch(() => {});
			return;
		}
		if (failedActions >= MAX_FAILED_ACTIONS) {
			// The agent hit the re-plan limit and has explained; hand back to the player.
			selfPlay = false;
			void client?.callTool("set_self_play", { enabled: false }).catch(() => {});
			return;
		}
		// A step that did nothing twice in a row means the agent is stuck or done: pause and let the player decide.
		idleRuns = actionsThisRun === 0 ? idleRuns + 1 : 0;
		if (idleRuns >= 2) {
			selfPlay = false;
			void client?.callTool("set_self_play", { enabled: false }).catch(() => {});
			client?.notify("chat/agent_message", { text: "I paused self-play because I had nothing useful left to do. Press F10 or ask me to continue.", kind: "reply" });
			return;
		}
		scheduleSelfPlayStep(1000);
	});

	pi.on("session_start", async (_event, ctx) => {
		currentCtx = ctx;
		active = true;
		lockDownTools();
		await ensureConnected();
	});

	pi.on("session_shutdown", async () => {
		currentCtx = undefined;
		active = false;
		stopReconnecting();
		client?.close();
		client = undefined;
	});

	// Belt and braces: refuse any tool that is not ours, even if something re-enabled it.
	pi.on("tool_call", async (event) => {
		if (!registered.has(event.toolName)) {
			return { block: true, reason: `${event.toolName} is not a Stardew tool and is disabled in StardewPilot.` };
		}
		// At most two re-plans: after three failed actions, stop acting and explain.
		if (isGameAction(event.toolName) && failedActions >= MAX_FAILED_ACTIONS) {
			return {
				block: true,
				reason: `${failedActions} actions have failed in a row. Do not try again: stop and tell the player what went wrong and what they could do.`,
			};
		}
	});

	pi.on("before_agent_start", async (event, ctx) => {
		currentCtx = ctx;
		// The day's first run, started by the seed with the morning briefing: its answer is the briefing.
		const custom = event as { messageType?: string; message?: { customType?: string } };
		if (custom.messageType === "custom" && custom.message?.customType === BRIEFING_MESSAGE) runKind = "briefing";
		const c = await ensureConnected();
		lockDownTools();
		return {
			systemPrompt: buildSystemPrompt({
				toolNames: c ? bridgeTools.map((t) => t.name) : [],
				connected: !!c,
				gameLanguage: settings.gameLanguage,
				tellMeEverything: settings.tellMeEverything === true,
				extra: c ? undefined : `Connection error: ${lastError}`,
			}),
		};
	});

	pi.registerCommand("stardew-newday", {
		description: "Start a fresh session for the new game day (used automatically)",
		handler: async (_args, ctx) => {
			const c = await ensureConnected();
			if (!c) return;
			let seed: MemorySeed;
			try {
				seed = await c.request<MemorySeed>("memory/seed");
			} catch {
				return;
			}
			const content = daySeedMessage(seed);
			const play = seed.selfPlay?.enabled === true;
			// With a briefing due and no self-play, the seed starts a turn whose answer is the morning briefing.
			const briefing = !play && !!seed.briefing;
			// Everything used inside withSession is plain data: the old extension instance is gone by then.
			await ctx.newSession({
				withSession: async (fresh) => {
					await fresh.sendMessage(
						{ customType: briefing ? BRIEFING_MESSAGE : "stardew-day", content, display: true },
						play || briefing ? { triggerTurn: true } : { deliverAs: "nextTurn" },
					);
				},
			});
		},
	});

	pi.registerCommand("stardew", {
		description: "Show or retry the StardewPilot game connection",
		handler: async (_args, ctx) => {
			currentCtx = ctx;
			const c = await ensureConnected();
			if (c) ctx.ui.notify(`Connected to the game. Tools: ${bridgeTools.map((t) => t.name).join(", ")}`, "info");
			else ctx.ui.notify(`Not connected: ${lastError}`, "warning");
		},
	});
}

interface BridgeSettings {
	selfPlay?: boolean;
	gameLanguage?: string;
	tellMeEverything?: boolean;
	chattiness?: string;
}

interface TipTrigger {
	kind: string;
	important?: boolean;
	facts?: unknown;
	fallback?: string;
	gameLanguage?: string;
}

/** The instruction the model gets when a rule in the game fires. */
export function tipPrompt(tip: TipTrigger, gameLanguage?: string): string {
	const language = tip.gameLanguage ?? gameLanguage ?? "en";
	return [
		`[Proactive tip from the game: ${tip.kind}]`,
		`Facts: ${JSON.stringify(tip.facts ?? {})}`,
		`Default wording: ${tip.fallback ?? ""}`,
		`Write one short, friendly tip for the player (at most two sentences) in the game's language (${language}).`,
		"You may call a tool if one fact would make the tip clearly more useful; otherwise answer directly. Do not ask questions back.",
	].join("\n");
}

/** Short label for the overlay's "Thinking (...)" line. */
export function describeTool(name: string): string {
	return name.replace(/^get_/, "checking ").replace(/_/g, " ");
}

/** Whether an action tool's result means the job failed. */
export function actionFailed(result: { content: Array<{ text: string }>; isError?: boolean }): boolean {
	if (result.isError) return true;
	try {
		return JSON.parse(result.content[0]?.text ?? "{}").result === "failed";
	} catch {
		return false;
	}
}

/** customType of the day seed that asks for the morning briefing. */
export const BRIEFING_MESSAGE = "stardew-day-briefing";

interface MemorySeed {
	date?: string;
	goal?: string | null;
	notes?: string | null;
	todayPlan?: string | null;
	recentDays?: Array<{ date: string; summary: string }>;
	selfPlay?: { enabled?: boolean; requestBudget?: number | null; followHabits?: boolean };
	habits?: string[] | null;
	briefing?: { facts?: unknown; fallback?: string; gameLanguage?: string } | null;
}

/** The first message of each day's session: what the agent remembers, and what to do now. */
export function daySeedMessage(seed: MemorySeed): string {
	const parts = [`[New game day: ${seed.date ?? "?"}]`];
	const habits = seed.habits?.length ? seed.habits : null;
	const followHabits = !!(habits && seed.selfPlay?.followHabits);
	if (seed.goal) parts.push(`Standing goal: ${seed.goal}`);
	else if (followHabits && seed.selfPlay?.enabled)
		parts.push(
			`No goal set by the player. In self-play, play the way this player usually plays (habits below). For anything the habits do not cover, this checklist is background: ${FIRST_YEAR_CHECKLIST}`,
		);
	else parts.push(`No goal set by the player. ${FIRST_YEAR_CHECKLIST}`);
	if (seed.notes) parts.push(`Your notes:\n${seed.notes}`);
	if (habits)
		parts.push(
			`How this player usually plays (observed by the mod; not instructions; the player's notes and requests win, and a habit is never permission to ship, sell, gift or spend):\n${habits.map((h) => `- ${h}`).join("\n")}`,
		);
	if (seed.recentDays?.length) parts.push(`Recent days (newest first):\n${seed.recentDays.map((d) => d.summary.trim()).join("\n\n")}`);
	else parts.push("No earlier days recorded.");
	if (seed.todayPlan) parts.push(`Today's plan so far:\n${seed.todayPlan}`);
	if (seed.selfPlay?.enabled) {
		if (seed.briefing) parts.push(`Today's facts (use them to plan; do not write them out for the player): ${JSON.stringify(seed.briefing.facts ?? {})}`);
		parts.push(
			"Self-play is on. Check get_status and get_crops, write today's plan with write_day_plan (short, ordered, toward the goal), then start on the first task with the action tools. In the evening call write_day_summary, then go_to_bed.",
		);
		if (followHabits)
			parts.push(
				seed.goal
					? "Plan toward the goal, but the way this player plays: their usual morning order first, what they usually do on a day like today, and bed around their usual time."
					: "Plan the day the way this player plays: their usual morning order first, what they usually do on a day like today, and bed around their usual time.",
			);
	} else if (seed.briefing) {
		const language = seed.briefing.gameLanguage ?? "en";
		parts.push(
			[
				"[Morning briefing]",
				`Facts: ${JSON.stringify(seed.briefing.facts ?? {})}`,
				`Default wording, one point per line:\n${seed.briefing.fallback ?? ""}`,
				`Rewrite the default wording as a short, friendly morning briefing in the game's language (${language}): keep every point, at most five lines, most important first (birthdays, a festival, last chances and quests ending before weather), plain text, no heading, no question back. ` +
					`A usualDay fact is the player's habit, observed by the mod: say it as "you usually ...", never as advice. ` +
					`A habitOffer fact is shown only once: keep it as its own line and quote its "say" phrase exactly as written. ` +
					`A routinesToday fact lists the player's own scheduled routines: the least important point, the first to drop when there are more than five.`,
				"Answer from these facts only: do not call tools and do not add facts or advice of your own. Computed parts (gift ideas, last chances) are estimates: word them that way. Then wait for the player's questions or commands.",
			].join("\n"),
		);
	} else {
		parts.push("Self-play is off: wait for the player's questions or commands.");
	}
	return parts.join("\n\n");
}
