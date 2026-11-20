# Test MCP server for PL/Pilot (see server.py). Not part of the plug-in.
FROM python:3.12-slim
RUN pip install --no-cache-dir "mcp>=1.12,<2"
WORKDIR /app
COPY server.py .
EXPOSE 8000
CMD ["python", "server.py"]
