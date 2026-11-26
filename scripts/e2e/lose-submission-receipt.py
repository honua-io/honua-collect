#!/usr/bin/env python3
"""Forward one emulator upload to Honua, then replace its success response with 503."""

import argparse
import asyncio
from pathlib import Path


async def relay(reader, writer):
    while data := await reader.read(65536):
        writer.write(data)
        await writer.drain()


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("server_port", type=int)
    parser.add_argument("form_id")
    parser.add_argument("state", type=Path)
    args = parser.parse_args()
    submission = f"POST /api/v1/forms/{args.form_id}/submissions ".encode()

    async def handle(client, downstream):
        upstream = None
        request_body = None
        try:
            request_line = await client.readline()
            if not request_line:
                return
            response, upstream = await asyncio.open_connection("127.0.0.1", args.server_port)
            upstream.write(request_line)
            request_body = asyncio.create_task(relay(client, upstream))

            # Forward interim responses (e.g. 100 Continue) before the final status.
            while True:
                headers = await response.readuntil(b"\r\n\r\n")
                status = int(headers.split(b" ", 2)[1])
                if status >= 200:
                    break
                downstream.write(headers)
                await downstream.drain()

            if request_line.startswith(submission) and 200 <= status < 300:
                (args.state / "withheld").touch()
                downstream.write(b"HTTP/1.1 503 Service Unavailable\r\n"
                                 b"Content-Length: 0\r\nConnection: close\r\n\r\n")
            else:
                downstream.write(headers)
                await relay(response, downstream)
            await downstream.drain()
        except (OSError, asyncio.IncompleteReadError):
            pass
        finally:
            if request_body is not None:
                request_body.cancel()
                await asyncio.gather(request_body, return_exceptions=True)
            if upstream is not None:
                upstream.close()
            downstream.close()

    async with await asyncio.start_server(handle, "127.0.0.1", 0) as server:
        (args.state / "port").write_text(str(server.sockets[0].getsockname()[1]))
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
