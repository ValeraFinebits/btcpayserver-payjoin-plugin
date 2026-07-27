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
            "description": "Return the BIP21 payment URL for a payable invoice, creating or reusing a PayJoin receiver session when PayJoin is available. Always branch on the status field rather than assuming the URL carries a PayJoin endpoint: any status other than Active returns a plain BIP21 URL. Note a non-Active status does not guarantee that no receiver session exists - some failures are detected after the session has been created, and it then remains until the invoice monitoring window expires.",
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
                "description": "BIP21 payment URL for the invoice. PayJoin-capable only when status is Active; every other status returns a plain BIP21 URL.",
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
              "directoryUrls",
              "ohttpRelayUrls"
            ],
            "properties": {
              "payjoinV2Enabled": {
                "type": "boolean",
                "description": "Whether checkout and API-generated payment URLs should include Payjoin v2 (BIP77) by default."
              },
              "directoryUrls": {
                "type": "array",
                "minItems": 1,
                "items": {
                  "type": "string",
                  "format": "uri"
                },
                "description": "PayJoin directory URLs."
              },
              "ohttpRelayUrls": {
                "type": "array",
                "minItems": 1,
                "items": {
                  "type": "string",
                  "format": "uri"
                },
                "description": "OHTTP relay URLs used for receiver polling."
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
              "status"
            ],
            "properties": {
              "bip21": {
                "type": "string",
                "description": "BIP21 payment URL. It includes the pjos and pj parameters when status is Active, and is a plain BIP21 URL for every other status."
              },
              "status": {
                "type": "string",
                "enum": [
                  "Active",
                  "DisabledByStore",
                  "MerchantRequirementsUnmet",
                  "TemporarilyUnavailable",
                  "InvoiceNotPayable"
                ],
                "description": "PayJoin availability for this invoice. Active means the returned BIP21 URL contains a supported PayJoin endpoint; any other value means the URL is plain BIP21. Replaces the former payjoinEnabled boolean, which has been removed: a client that must support both server versions can treat the presence of this field as the signal, and must not read a missing payjoinEnabled as 'PayJoin unavailable'."
              },
              "unavailableReason": {
                "type": "string",
                "nullable": true,
                "description": "Human-readable reason why PayJoin is unavailable. Null when status is Active."
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
