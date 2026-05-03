using BTCPayServer.Abstractions.Contracts;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Payjoin.Services;

public sealed class PayjoinSwaggerProvider : ISwaggerProvider
{
    private const string SwaggerJson = """
    {
      "tags": [
        {
          "name": "PayJoin",
          "description": "PayJoin receiver settings and invoice payment URLs"
        }
      ],
      "paths": {
        "/api/v1/stores/{storeId}/payjoin/settings": {
          "get": {
            "tags": [
              "PayJoin"
            ],
            "summary": "Get PayJoin settings",
            "description": "View PayJoin receiver settings for the specified store.",
            "operationId": "PayJoin_GetSettings",
            "parameters": [
              {
                "$ref": "#/components/parameters/StoreId"
              }
            ],
            "responses": {
              "200": {
                "description": "PayJoin settings for the store",
                "content": {
                  "application/json": {
                    "schema": {
                      "$ref": "#/components/schemas/PayjoinStoreSettingsData"
                    }
                  }
                }
              },
              "403": {
                "description": "If you are authenticated but forbidden to view the specified store settings"
              },
              "404": {
                "description": "The store was not found"
              }
            },
            "security": [
              {
                "API_Key": [
                  "btcpay.store.canviewstoresettings"
                ],
                "Basic": []
              }
            ]
          },
          "put": {
            "tags": [
              "PayJoin"
            ],
            "summary": "Update PayJoin settings",
            "description": "Update PayJoin receiver settings for the specified store.",
            "operationId": "PayJoin_UpdateSettings",
            "parameters": [
              {
                "$ref": "#/components/parameters/StoreId"
              }
            ],
            "requestBody": {
              "required": true,
              "content": {
                "application/json": {
                  "schema": {
                    "$ref": "#/components/schemas/PayjoinStoreSettingsData"
                  }
                }
              }
            },
            "responses": {
              "200": {
                "description": "Updated PayJoin settings for the store",
                "content": {
                  "application/json": {
                    "schema": {
                      "$ref": "#/components/schemas/PayjoinStoreSettingsData"
                    }
                  }
                }
              },
              "403": {
                "description": "If you are authenticated but forbidden to update the specified store settings"
              },
              "404": {
                "description": "The store was not found"
              },
              "422": {
                "description": "A list of validation errors that occurred when updating PayJoin settings",
                "content": {
                  "application/json": {
                    "schema": {
                      "$ref": "#/components/schemas/ValidationProblemDetails"
                    }
                  }
                }
              }
            },
            "security": [
              {
                "API_Key": [
                  "btcpay.store.canmodifystoresettings"
                ],
                "Basic": []
              }
            ]
          }
        },
        "/api/v1/stores/{storeId}/invoices/{invoiceId}/payjoin/payment-url": {
          "get": {
            "tags": [
              "PayJoin"
            ],
            "summary": "Get invoice PayJoin payment URL",
            "description": "Create or reuse the active PayJoin receiver session for a payable invoice and return its BIP21 payment URL.",
            "operationId": "PayJoin_GetInvoicePaymentUrl",
            "parameters": [
              {
                "$ref": "#/components/parameters/StoreId"
              },
              {
                "$ref": "#/components/parameters/InvoiceId"
              }
            ],
            "responses": {
              "200": {
                "description": "PayJoin-capable BIP21 payment URL for the invoice",
                "content": {
                  "application/json": {
                    "schema": {
                      "$ref": "#/components/schemas/PayjoinPaymentUrlData"
                    }
                  }
                }
              },
              "403": {
                "description": "If you are authenticated but forbidden to view the specified invoice"
              },
              "404": {
                "description": "The invoice was not found or no PayJoin payment URL is available"
              }
            },
            "security": [
              {
                "API_Key": [
                  "btcpay.store.canviewinvoices"
                ],
                "Basic": []
              }
            ]
          }
        }
      },
      "components": {
        "schemas": {
          "PayjoinStoreSettingsData": {
            "type": "object",
            "additionalProperties": false,
            "required": [
              "directoryUrl",
              "ohttpRelayUrl"
            ],
            "properties": {
              "enabledByDefault": {
                "type": "boolean",
                "description": "Whether checkout and API-generated payment URLs should include PayJoin by default."
              },
              "directoryUrl": {
                "type": "string",
                "format": "uri",
                "description": "PayJoin directory URL."
              },
              "ohttpRelayUrl": {
                "type": "string",
                "format": "uri",
                "description": "OHTTP relay URL used for receiver polling."
              },
              "coldWalletDerivationScheme": {
                "type": "string",
                "nullable": true,
                "description": "Optional BTC derivation scheme used for receiver change outputs."
              }
            }
          },
          "PayjoinPaymentUrlData": {
            "type": "object",
            "additionalProperties": false,
            "required": [
              "bip21",
              "payjoinEnabled"
            ],
            "properties": {
              "bip21": {
                "type": "string",
                "description": "BIP21 payment URL. When PayJoin is enabled this includes pjos and pj parameters."
              },
              "payjoinEnabled": {
                "type": "boolean",
                "description": "Whether the returned BIP21 URL contains a supported PayJoin endpoint."
              }
            }
          }
        }
      }
    }
    """;

    public Task<JObject> Fetch()
    {
        return Task.FromResult(JObject.Parse(SwaggerJson));
    }
}
