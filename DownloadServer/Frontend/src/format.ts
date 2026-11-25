/**
 * What the download server's pages say alike of what is offered and fetched.
 */

/** A number of bytes as somebody reads it: "512 B", "1.4 MiB". */
export function sizeOf(Bytes: number): string {

    if (!Number.isFinite(Bytes) || Bytes < 0)
        return '-';

    if (Bytes < 1024)
        return `${Bytes} B`;

    const units = [ 'KiB', 'MiB', 'GiB', 'TiB' ];

    let value = Bytes / 1024;
    let unit  = 0;

    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }

    return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;

}

/** Where this web interface was reached at: what a charging station would be told to download from, failing a better name. */
export function thisHost(): string {
    return typeof location !== 'undefined' && location.hostname.length > 0 ? location.hostname : 'downloads.example.org';
}
