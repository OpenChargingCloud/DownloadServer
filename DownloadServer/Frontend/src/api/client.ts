import { apiURL,
         nodeAPI,
         request,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module. What follows is what a download server says
// of its own: its resources, its configuration's own section, the kinds its
// store keeps - and the files, the downloads and the downloaders, below
// "/downloadServer".
export * from '@node/api/client';


/**
 * What a role may be allowed to touch on this download server: what every
 * node has, and the files - read, listed and saved; edited, published and
 * taken away - the record of the downloads - read -, the downloaders - read;
 * edited, made, given a new password, taken away - and downloading itself,
 * which charging stations run.
 */
export type Resource = NodeResource | 'files' | 'downloads' | 'downloaders' | 'download';

/** What somebody signed in to this download server may do: an operation on a resource, written "files:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the node is doing right now: what every node says. */
export type Status = NodeStatus;

/**
 * What the download server is made of: every node's sections, and its own.
 * Only the shape the Configuration page relies on is named; the fields of
 * each section are drawn from whatever the server sends.
 */
export interface Configuration extends NodeConfiguration {
    downloadServer:  Record<string, unknown>;
}

/** What a certificate in the node's own store is for: the four kinds of TLS. */
export type CertificateKind = 'tlsRoot' | 'clientRoot' | 'tlsServer' | 'tlsIdentity';

/** One certificate in the node's own store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import into the node's own store sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

/** The node's own store, grouped the way it is shown. */
export type CertificateStore = NodeCertificateStore<CertificateKind>;


/** One file offered. */
export interface OfferedFile {
    area:           string;
    name:           string;
    /** What it is fetched by: "/firmware/station-4.2.1.bin". */
    path:           string;
    /** In bytes. */
    size:           number;
    /** When it was last written - "2026-11-24T10:15:00.123Z". */
    modified:       string;
    /** In lower-case hex. */
    sha256:         string;
    /** How often it was downloaded, where the list says. */
    downloads?:     number;
    lastDownload?:  string | null;
}

/** What GET /downloadServer/files answers. */
export interface Files {
    /** Every area there is. */
    areas:  string[];
    /** The files of the area asked for, or of every one. */
    files:  OfferedFile[];
}

/** What publishing a file answers. */
export interface Published {
    said:  string;
    file:  OfferedFile;
}

/** How a file was fetched. */
export type DownloadProtocol = 'HTTP' | 'SFTP';

/** One download, and how it went. */
export interface Download {
    /** Its number: one above the one before, never reused. */
    id:             number;
    timestamp:      string;
    protocol:       DownloadProtocol;
    /** The account it was fetched under, or "anonymous". */
    downloader:     string;
    area:           string;
    fileName:       string;
    /** 200 for the file from its first byte on, 206 for the rest of it. */
    status:         number;
    /** Where what was sent begins. */
    firstByte:      number;
    /** How many bytes were sent. */
    bytes:          number;
    /** How large the file was. */
    size:           number;
    /** Whether what was sent reaches the end of the file. */
    toTheEnd:       boolean;
    remoteAddress:  string;
    userAgent?:     string;
}

/** Which downloads are asked for: GET /downloadServer/downloads. */
export interface DownloadFilter {
    downloader?:  string;
    area?:        string;
    file?:        string;
    /** ISO 8601: at or after. */
    since?:       string;
    /** ISO 8601: before. */
    until?:       string;
    order?:       'newest' | 'oldest';
    skip?:        number;
    take?:        number;
}

/** What GET /downloadServer/downloads answers. */
export interface Downloads {
    /** How many match the filter in all. */
    total:        number;
    /** Those of them asked for, in the order asked for. */
    downloads:    Download[];
    /** Every account that ever downloaded - for the filter to offer. */
    downloaders:  string[];
    /** Every file ever downloaded, by its path - for the filter to offer. */
    files:        string[];
}

/** The certificate the download port shows - its own, not the web interface's. */
export interface DownloadPortCertificate {
    /** "store", or "files". */
    certificate:  string;
    fullchain:    string | null;
    privateKey:   string | null;
    /** The label of the store's identity shown. */
    label:        string | null;
    /** The subject of the certificate shown, or null while there is none. */
    shows:        string | null;
    thumbprint:   string | null;
    notAfter:     string | null;
    /** Why nothing, or not the newest, is shown. */
    problem:      string | null;
}

/** The port HTTP downloads are served on. */
export interface DownloadPort {
    listening:    boolean;
    /** The root charging stations are told to download below: "publicURL", or a guess. */
    url:          string;
    /** What "downloadServer.publicURL" says, or null where the URL is guessed. */
    publicURL:    string | null;
    /** Where the port listens: "http://any:8365/". */
    listen:       string;
    tls:          boolean;
    certificate:  DownloadPortCertificate | null;
    anonymous:    boolean;
    refused:      number;
}

/** How the download server stands: GET /downloadServer. */
export interface DownloadServerStatus {
    /** The directory the areas lie in. */
    directory:        string;
    areas:            { name: string; files: number; size: number }[];
    /** The port HTTP downloads are served on, or null where they are not. */
    http:             DownloadPort | null;
    /** SFTP on the SSH server, or null where files are not offered over it. */
    sftp:             { serving: boolean; port: number | null; passwords: boolean } | null;
    /** The largest file that may be published, in bytes. */
    maxFileSize:      number;
    /** How large the parts are the web interface sends a larger file in, in bytes: what its HTTP server takes in one request, with room to spare. */
    webPartSize:      number;
    files:            number;
    /** In bytes. */
    totalSize:        number;
    downloads:        number;
    /** In bytes. */
    downloadedBytes:  number;
    downloaders:      number;
    latest:           Download | null;
}

/** An account in the downloader role. */
export interface Downloader {
    id:            string;
    name:          string;
    disabled:      boolean;
    downloads:     number;
    lastDownload:  string | null;
}

/** What a change of the downloaders answers: what it said, the password where one was made - once - and the downloaders as they are now. */
export interface DownloadersChanged {
    said:         string;
    downloader:   string | null;
    password:     string | null;
    downloaders:  Downloader[];
}


/** The query string of a filter: only what it says. */
function queryOf(Filter: object): string {

    const query = new URLSearchParams();

    for (const [key, value] of Object.entries(Filter))
        if (value !== undefined && value !== null && value !== '')
            query.set(key, String(value));

    const text = query.toString();

    return text.length > 0 ? `?${text}` : '';

}

/** The path of a file below the files of the API. */
function filePath(Area: string, Name: string): string {
    return `/downloadServer/files/${encodeURIComponent(Area)}/${encodeURIComponent(Name)}`;
}


/** A PUT the download server answered with an error: its status, and - for a part at another offset - how many bytes it has. */
export class PutRefused extends Error {

    readonly Status:    number;
    readonly Received:  number | null;

    constructor(Message: string, Status: number, Received: number | null) {
        super(Message);
        this.name      = 'PutRefused';
        this.Status    = Status;
        this.Received  = Received;
    }

}

/** One PUT of bytes, as they are - which the JSON request of every node does not send - and its answer. Same origin, so the session cookie travels with it. */
async function put<T>(Path: string, Content: Blob): Promise<T> {

    const response = await fetch(apiURL(Path), {
                               method:       'PUT',
                               headers:      { 'Accept': 'application/json', 'Content-Type': 'application/octet-stream' },
                               credentials:  'same-origin',
                               body:         Content
                           });

    const text = await response.text();

    let json: unknown = null;

    try { json = text.length > 0 ? JSON.parse(text) : null; } catch { json = null; }

    if (!response.ok) {

        // 408 is Hermod's, in words about the request rather than the file.
        const said = response.status === 408
                         ? 'A part took longer than the web interface waits for one - five minutes, unless "web.requestBodyTimeout" says otherwise. The line is too slow even for the smallest part.'
                         : json !== null && typeof json === 'object' && 'error' in json && typeof (json as { error: unknown }).error === 'string'
                               ? (json as { error: string }).error
                               : response.status === 413
                                     ? 'A part is larger than the web interface takes.'
                                     : `The download server answered ${response.status}.`;

        const received = json !== null && typeof json === 'object' && 'received' in json && typeof (json as { received: unknown }).received === 'number'
                             ? (json as { received: number }).received
                             : null;

        throw new PutRefused(said, response.status, received);

    }

    return json as T;

}


/** The smallest part a file is sent in: what the server never asks to be smaller, either. */
export const SmallestPartSize = 64 * 1024;

/** How often in a row a part may fail for the time it took before the publication is given up. */
const MostSlowPartsInARow = 8;

/**
 * Whether a PUT failed for the time it took: answered 408 - the web
 * interface's HTTP server waits five minutes for one request's body unless
 * "web.requestBodyTimeout" says otherwise - or
 * broken off without an answer, which is how a browser still sending tells
 * of a server that answered 408 and closed the connection.
 */
function tookTooLong(Problem: unknown): boolean {
    return Problem instanceof PutRefused
               ? Problem.Status === 408
               : Problem instanceof TypeError;
}

/**
 * Publish a file: whole with one PUT where it fits into one part, and else in
 * parts, one after the other - "?offset=" says where each begins, the last
 * one says "&final=true" -, as the web interface's HTTP server takes no more
 * than so much in one request. Told after each part how many bytes are there.
 *
 * Over a slow line a part may take longer than that server waits for one
 * request's body, five minutes unless "web.requestBodyTimeout" says
 * otherwise: the part is sent again, half as large - down
 * to 64 KiB -, and nothing of it was kept. Where the server has more than was
 * thought - an answer lost on the way -, it says how much (409), and the next
 * part begins there.
 */
async function publish(Area:      string,
                       Name:      string,
                       Content:   Blob,
                       PartSize:  number,
                       Progress?: (Sent: number, Of: number) => void): Promise<Published> {

    let partSize  = Math.max(SmallestPartSize, PartSize);
    let offset    = 0;
    let failed    = 0;

    while (true) {

        try
        {

            if (offset === 0 && Content.size <= partSize)
                return await put<Published>(filePath(Area, Name), Content);

            const end    = Math.min(offset + partSize, Content.size);
            const final  = end >= Content.size;
            const done   = await put<Published & { received: number }>(`${filePath(Area, Name)}?offset=${offset}${final ? '&final=true' : ''}`,
                                                                        Content.slice(offset, end));

            Progress?.(done.received, Content.size);

            if (final)
                return done;

            offset  = done.received;
            failed  = 0;

        }
        catch (problem)
        {

            if (++failed > MostSlowPartsInARow)
                throw problem;

            if (problem instanceof PutRefused && problem.Status === 409 && problem.Received !== null && offset > 0) {
                offset = problem.Received;
                continue;
            }

            if (!tookTooLong(problem) || partSize <= SmallestPartSize)
                throw problem;

            partSize = Math.max(SmallestPartSize, Math.floor(partSize / 2));

        }

    }

}


/** How the download server is asked: the routes every node has, typed with the server's own of them, and the files, downloads and downloaders. */
export const api = {

    ...nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>(),

    downloadServer: {

        status:            ()                                     => request<DownloadServerStatus>  ('GET',  '/downloadServer'),

        files:             (Area?: string)                        => request<Files>                 ('GET',  '/downloadServer/files' + queryOf({ area: Area })),
        publish,
        removeFile:        (Area: string, Name: string)           => request<{ said: string }>      ('POST', `${filePath(Area, Name)}/remove`, {}),

        /** Where a file is, to save it. */
        contentURL:        (Area: string, Name: string)           => apiURL(`${filePath(Area, Name)}/content`),

        downloads:         (Filter: DownloadFilter)               => request<Downloads>             ('GET',  '/downloadServer/downloads' + queryOf(Filter)),

        downloaders:       ()                                     => request<Downloader[]>          ('GET',  '/downloadServer/downloaders'),
        addDownloader:     (Id: string, Name?: string)            => request<DownloadersChanged>    ('POST', '/downloadServer/downloaders', Name !== undefined && Name.length > 0 ? { id: Id, name: Name } : { id: Id }),
        newPassword:       (Id: string)                           => request<DownloadersChanged>    ('POST', '/downloadServer/downloaders/password', { id: Id }),
        removeDownloader:  (Id: string)                           => request<DownloadersChanged>    ('POST', '/downloadServer/downloaders/remove', { id: Id })

    }

};
