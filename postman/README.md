# Postman collection

Import [`P5-aSTEP.postman_collection.json`](./P5-aSTEP.postman_collection.json)
into Postman. It contains local requests for both services:

- Python model service at `http://127.0.0.1:8001`;
- ASP.NET API at `http://localhost:5073`.

Start both services before running the collection. The collection variables
`pythonBaseUrl` and `apiBaseUrl` can be changed for another local port or
deployment.

The `.joblib` files and local model configuration remain outside this
collection. Do not put model paths, credentials, or `.env` contents into the
collection file.
