/**
 * Welcome to Cloudflare Workers! This is your first worker.
 *
 * - Run `npm run dev` in your terminal to start a development server
 * - Open a browser tab at http://localhost:8787/ to see your worker in action
 * - Run `npm run deploy` to publish your worker
 *
 * Learn more at https://developers.cloudflare.com/workers/
 */

import {
	error,      // creates error responses
	json,       // creates JSON responses
	Router,     // the ~440 byte router itself
	withParams, // middleware: puts params directly on the Request
} from 'itty-router'
import { GitHub } from './github';

export interface Env {
	DISCORD_BOT_TOKEN: string;
	GITHUB_WEBHOOK_SECRET: string;
	KV_DATABASE: KVNamespace;
}

// create a new Router
const router = Router()
router
	.all('*', withParams)
	.post('/webhook', GitHub.signatureCheck)
	.get('/todos', () => todos)
	.get(
		'/todos/:id',
		({ id }) => todos.getById(id) || error(404, 'That todo was not found')
	)
	// 404 for everything else
	.all('*', () => error(404))

export default {
	async fetch(request: Request, env: Env, context: ExecutionContext): Promise<Response> {
		const url = new URL(request.url);


		return new Response('Hello World Test!');
	},
};
