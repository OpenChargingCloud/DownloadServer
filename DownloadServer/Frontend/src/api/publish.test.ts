/**
 * How the web interface publishes a file larger than one request may be: in
 * parts, one after the other - and, over a line too slow for the half minute
 * the web interface's HTTP server waits for one request's body, in smaller
 * ones, without a byte sent twice into the file.
 *
 * Run with `npm test`: Node's own runner, a fetch of the test's own in front
 * of the client.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

// The client reads WWCP_Node's config.ts, which reads <meta> tags when it is
// loaded: none here, and the client falls back to its own defaults.
(globalThis as unknown as { document: unknown }).document = {
    querySelector: () => null
};

const { api, PutRefused, SmallestPartSize } = await import('./client.ts');


/** One PUT as the download server saw it. */
interface Asked { offset: number | null; final: boolean; size: number }

/**
 * A download server that takes parts as the real one does - each at the
 * offset the parts before ended at - but answers 408 to every part larger
 * than it can take in time, as a slow line makes it.
 */
function aServerThatTakesAtMost(Largest: number, Asked: Asked[]): void {

    let received = 0;

    (globalThis as unknown as { fetch: unknown }).fetch =
        async (url: string, init: RequestInit) => {

            const query   = new URL(url, 'http://localhost/').searchParams;
            const offset  = query.has('offset') ? Number(query.get('offset')) : null;
            const final   = query.get('final') === 'true';
            const size    = (init.body as Blob).size;

            Asked.push({ offset, final, size });

            const answer  = (Status: number, JSON_: object) => new Response(JSON.stringify(JSON_), { status: Status });

            if (size > Largest)
                return answer(408, { error: 'Request timeout' });

            if (offset === null) {
                received = size;
                return answer(200, { said: 'offered', file: { size } });
            }

            if (offset !== 0 && offset !== received)
                return answer(409, { error: 'at another offset', received });

            received = offset + size;

            return answer(200, final ? { said: 'offered', received, file: { size: received } } : { received });

        };

}


describe('publishing in parts', () => {

    it('sends the parts one after the other, at the offsets the parts before ended at', async () => {

        const asked: Asked[] = [];
        aServerThatTakesAtMost(Number.MAX_SAFE_INTEGER, asked);

        const published = await api.downloadServer.publish('firmware', 'a.bin', new Blob([ new Uint8Array(1000 * 1024) ]), 400 * 1024);

        assert.deepEqual(asked.map(part => [ part.offset, part.final ]), [ [ 0, false ], [ 400 * 1024, false ], [ 800 * 1024, true ] ]);
        assert.equal((published as unknown as { received: number }).received, 1000 * 1024);

    });

    it('sends smaller parts where one takes longer than the server waits, and nothing twice', async () => {

        const asked: Asked[] = [];
        aServerThatTakesAtMost(300 * 1024, asked);

        const size       = 1000 * 1024;
        const sent: number[] = [];
        const published  = await api.downloadServer.publish('firmware', 'a.bin', new Blob([ new Uint8Array(size) ]), 1024 * 1024,
                                                            (Sent) => sent.push(Sent));

        // The whole file at once, then 512 KiB, refused; 256 KiB parts from then on.
        assert.deepEqual(asked.slice(0, 2).map(part => part.size), [ size, 512 * 1024 ]);
        assert.ok(asked.slice(2).every(part => part.size <= 256 * 1024));
        assert.equal(asked.at(-1)!.final, true);

        const taken = asked.filter(part => part.size <= 300 * 1024).reduce((sum, part) => sum + part.size, 0);
        assert.equal(taken, size, 'every byte taken once');
        assert.equal(sent.at(-1), size);
        assert.equal((published as unknown as { received: number }).received, size);

    });

    it('gives up where even the smallest part takes too long, saying why', async () => {

        const asked: Asked[] = [];
        aServerThatTakesAtMost(SmallestPartSize - 1, asked);

        await assert.rejects(api.downloadServer.publish('firmware', 'a.bin', new Blob([ new Uint8Array(512 * 1024) ]), 256 * 1024),
                             (problem: unknown) => problem instanceof PutRefused && problem.Status === 408 &&
                                                   /longer than the web interface waits/.test(problem.message));

        assert.equal(asked.at(-1)!.size, SmallestPartSize);

    });

    it('does not send smaller parts where the server refuses for another reason', async () => {

        (globalThis as unknown as { fetch: unknown }).fetch =
            async () => new Response(JSON.stringify({ error: 'There is no area \'nowhere\'.' }), { status: 404 });

        await assert.rejects(api.downloadServer.publish('nowhere', 'a.bin', new Blob([ new Uint8Array(1024) ]), 256 * 1024),
                             /There is no area/);

    });

});
