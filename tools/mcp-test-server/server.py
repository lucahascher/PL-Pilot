"""Test MCP server for PL/Pilot: a small ticket system with version control history, built with the
official MCP Python SDK (FastMCP), so the plug-in's own MCP client is tested against the reference
implementation.

    docker build -t plpilot-mcp-test tools/mcp-test-server
    # Streamable HTTP, with a bearer token:   URL http://localhost:8801/mcp, header "Authorization: Bearer test-token"
    docker run -d --name plpilot-mcp-test -p 8801:8000 -e MCP_TOKEN=test-token plpilot-mcp-test
    # the old HTTP+SSE transport:              URL http://localhost:8802/sse
    docker run -d --name plpilot-mcp-test-sse -p 8802:8000 -e MCP_TRANSPORT=sse plpilot-mcp-test
"""
import asyncio
import os

import uvicorn
from mcp.server.fastmcp import Context, FastMCP
from mcp.types import ToolAnnotations

mcp = FastMCP(
    "tickets-test",
    instructions=(
        "Test ticket system. Ticket keys look like PLS-123. get_ticket returns the ticket with its linked commits; "
        "get_commit shows the change of a commit; search_commits searches the version control history."
    ),
)

TICKETS = {
    "PLS-101": {
        "title": "Employee list per department is slow and unsorted",
        "status": "open",
        "reporter": "HR team",
        "description": (
            "EMP_API.list_emps reads every column of EMP although it only prints the employee name. "
            "The list must be sorted by name. Since commit a3c9e10 the department filter is used, the table "
            "has grown to 2 million rows."
        ),
        "commits": ["a3c9e10"],
        "comments": [],
    },
    "PLS-102": {
        "title": "Audit columns for EMP",
        "status": "done",
        "reporter": "Auditing",
        "description": "Add created_by / created_at to EMP.",
        "commits": ["77d01be"],
        "comments": [],
    },
}

COMMITS = {
    "a3c9e10": {
        "date": "2026-10-02",
        "author": "m.mueller",
        "message": "PLS-101 list_emps: filter by department",
        "diff": (
            "--- a/db/emp_api.pkb\n+++ b/db/emp_api.pkb\n@@ procedure list_emps @@\n"
            "-    for r in (select * from emp e) loop\n"
            "+    for r in (select * from emp e where e.deptno = p_deptno) loop\n"
        ),
    },
    "77d01be": {
        "date": "2026-09-12",
        "author": "a.schmidt",
        "message": "PLS-102 add audit columns to EMP",
        "diff": "--- a/db/emp.sql\n+++ b/db/emp.sql\n+alter table emp add (created_by varchar2(30), created_at date);\n",
    },
}


@mcp.tool(annotations=ToolAnnotations(title="Get ticket", readOnlyHint=True))
def get_ticket(key: str) -> str:
    """Reads a ticket: title, status, description, comments and the commits linked to it."""
    t = TICKETS.get(key.strip().upper())
    if not t:
        raise ValueError(f"Ticket {key} does not exist.")
    lines = [f"{key.upper()}: {t['title']}", f"Status: {t['status']}", f"Reporter: {t['reporter']}",
             f"Description: {t['description']}", "Linked commits: " + ", ".join(t["commits"])]
    lines += [f"Comment: {c}" for c in t["comments"]]
    return "\n".join(lines)


@mcp.tool(annotations=ToolAnnotations(title="Search tickets", readOnlyHint=True))
def search_tickets(text: str) -> str:
    """Searches ticket titles and descriptions."""
    hits = [f"{k}: {t['title']} ({t['status']})" for k, t in TICKETS.items()
            if text.lower() in (t["title"] + " " + t["description"]).lower()]
    return "\n".join(hits) or "No tickets found."


@mcp.tool(annotations=ToolAnnotations(title="Search commits", readOnlyHint=True))
async def search_commits(query: str, ctx: Context) -> str:
    """Searches the version control history (commit messages and changed files)."""
    try:
        await ctx.report_progress(1, 2, message="searching the version control history")
    except TypeError:  # older SDK without progress messages
        await ctx.report_progress(1, 2)
    await asyncio.sleep(0.5)
    hits = [f"{cid} {c['date']} {c['author']}: {c['message']}" for cid, c in COMMITS.items()
            if query.lower() in (c["message"] + c["diff"]).lower()]
    return "\n".join(hits) or "No commits found."


@mcp.tool(annotations=ToolAnnotations(title="Get commit", readOnlyHint=True))
def get_commit(commit_id: str) -> str:
    """Shows a commit: author, date, message and the diff."""
    c = COMMITS.get(commit_id.strip())
    if not c:
        raise ValueError(f"Commit {commit_id} does not exist.")
    return f"commit {commit_id}\nAuthor: {c['author']}\nDate: {c['date']}\n\n    {c['message']}\n\n{c['diff']}"


@mcp.tool(annotations=ToolAnnotations(title="Add comment", readOnlyHint=False, destructiveHint=False))
def add_comment(key: str, comment: str) -> str:
    """Adds a comment to a ticket."""
    t = TICKETS.get(key.strip().upper())
    if not t:
        raise ValueError(f"Ticket {key} does not exist.")
    t["comments"].append(comment)
    return f"Comment added to {key.upper()}."


@mcp.resource("tickets://ticket/{key}", name="ticket", description="A ticket by its key", mime_type="text/plain")
def ticket_resource(key: str) -> str:
    """Resource template: @tickets:ticket/PLS-101 (or just @tickets:PLS-101) in PL/Pilot."""
    return get_ticket(key)


@mcp.resource("tickets://open", name="open-tickets", description="All open tickets", mime_type="text/plain")
def open_tickets() -> str:
    """Static resource: @tickets:open-tickets in PL/Pilot."""
    return "\n".join(f"{k}: {t['title']}" for k, t in TICKETS.items() if t["status"] == "open") or "No open tickets."


@mcp.prompt(title="Review the code of a ticket")
def review_ticket(key: str, focus: str = "") -> str:
    """Prompt: /tickets:review_ticket PLS-101 [focus] in PL/Pilot."""
    return (f"Read ticket {key} and the commits linked to it with the ticket tools, then review the PL/SQL code it concerns"
            + (f", with a focus on {focus}" if focus else "") + ". List the problems with line references. Do not change anything.")


def require_token(app, token):
    """Rejects HTTP requests without "Authorization: Bearer <token>" (to test configured headers)."""
    async def wrapped(scope, receive, send):
        if scope["type"] == "http" and token:
            auth = dict(scope.get("headers") or []).get(b"authorization", b"").decode()
            if auth != f"Bearer {token}":
                await send({"type": "http.response.start", "status": 401, "headers": [(b"content-type", b"text/plain")]})
                await send({"type": "http.response.body", "body": b"missing or wrong bearer token"})
                return
        await app(scope, receive, send)
    return wrapped


if __name__ == "__main__":
    transport = os.environ.get("MCP_TRANSPORT", "streamable-http")
    app = mcp.sse_app() if transport == "sse" else mcp.streamable_http_app()
    uvicorn.run(require_token(app, os.environ.get("MCP_TOKEN", "")), host="0.0.0.0", port=8000)
