import * as crypto from "crypto";
import { error, json } from "itty-router";

export class GitHub {
	public static signatureCheck(request: Request, secret: string): Response | null {
		const signatureHeader = request.headers.get("x-hub-signature-256");
		if (signatureHeader === null) {
			error(400, json({ error: "No signature" }));
		}

		const body = request.body;
		if (body === null) {
			error(400, json({ error: "No body" }));
		}

		const signature = crypto
			.createHmac("sha256", secret)
			.update(JSON.stringify(request.body))
			.digest("hex");

		let trusted = Buffer.from(`sha256=${signature}`, 'ascii');
		let untrusted = Buffer.from(`${request.headers.get("x-hub-signature-256")}`, 'ascii');
		if (!crypto.timingSafeEqual(trusted, untrusted)) {
			error(401, json({ error: "Invalid signature" }));
		}

		return null;
	}
}
