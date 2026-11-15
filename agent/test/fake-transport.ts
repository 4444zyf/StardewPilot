import { readFileSync } from "node:fs";
import type { Transport } from "../src/bridge-client.ts";

type Recording = {
	initialize: unknown;
	"tools/list": unknown;
	"tools/call": Record<string, unknown>;
};

/** Replays recorded bridge responses. Records every request it receives. */
export class ReplayTransport implements Transport {
	readonly sent: Array<{ id?: number; method: string; params?: any }> = [];
	private messageHandlers: Array<(text: string) => void> = [];
	private closeHandlers: Array<(reason: string) => void> = [];
	private readonly recording: Recording;

	constructor(fixturePath = new URL("./fixtures/bridge-recording.json", import.meta.url)) {
		this.recording = JSON.parse(readFileSync(fixturePath, "utf8"));
	}

	send(text: string): void {
		const msg = JSON.parse(text);
		this.sent.push(msg);
		if (msg.id === undefined) return;
		let result: unknown;
		let error: { code: number; message: string } | undefined;
		if (msg.method === "tools/call") {
			result = this.recording["tools/call"][msg.params.name];
			if (!result) error = { code: -32000, message: `no recording for ${msg.params.name}` };
		} else if (msg.method in this.recording) {
			result = (this.recording as any)[msg.method];
		} else {
			error = { code: -32601, message: `Unknown method '${msg.method}'.` };
		}
		const reply = JSON.stringify(error ? { jsonrpc: "2.0", id: msg.id, error } : { jsonrpc: "2.0", id: msg.id, result });
		queueMicrotask(() => this.emit(reply));
	}

	/** Push a server-originated message. */
	emit(text: string): void {
		for (const h of this.messageHandlers) h(text);
	}

	close(): void {
		for (const h of this.closeHandlers) h("closed by client");
	}

	onMessage(handler: (text: string) => void): void {
		this.messageHandlers.push(handler);
	}

	onClose(handler: (reason: string) => void): void {
		this.closeHandlers.push(handler);
	}
}

/** A transport that never answers, for timeout and close tests. */
export class SilentTransport extends ReplayTransport {
	override send(text: string): void {
		this.sent.push(JSON.parse(text));
	}
}
