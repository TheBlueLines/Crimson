using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Crimson.Views;

internal static class StoreWebIntegration
{
	private const string Script = """
		(() => {
			const installerPath = '/launcher/api/installer/download/';
			const observedRoots = new WeakSet();
			const css = `
				.main-cta:has(epic-wf-cta-button[href*="${installerPath}"]),
				epic-wf-cta-button[href*="${installerPath}"],
				a[part~="root"][href*="${installerPath}"] {
					display: none !important;
				}
				.ReactModal__Overlay:has(a[href*="${installerPath}"]) {
					display: none !important;
				}
				body.ReactModal__Body--open:has(.ReactModal__Overlay a[href*="${installerPath}"]) {
					overflow: auto !important;
				}
			`;

			function closeLauncherModal(root) {
				root
					.querySelector(`.ReactModal__Overlay:has(a[href*="${installerPath}"]) button[aria-label="Close modal"]`)
					?.click();
			}

			function observeRoot(root) {
				if (observedRoots.has(root)) {
					return;
				}

				observedRoots.add(root);
				const style = document.createElement('style');
				style.textContent = css;
				(root instanceof Document ? root.documentElement : root).appendChild(style);

				root.querySelectorAll('*').forEach(element => {
					if (element.shadowRoot) {
						observeRoot(element.shadowRoot);
					}
				});

				new MutationObserver(() => closeLauncherModal(root)).observe(root, {
					childList: true,
					subtree: true
				});
				closeLauncherModal(root);
			}

			const attachShadow = Element.prototype.attachShadow;
			Element.prototype.attachShadow = function(options) {
				const root = attachShadow.call(this, options);
				queueMicrotask(() => observeRoot(root));
				return root;
			};

			const initialize = () => {
				if (!document.documentElement) {
					setTimeout(initialize, 0);
					return;
				}

				observeRoot(document);
			};

			initialize();
		})();
		""";

	public static async Task ApplyAsync(CoreWebView2 webView)
	{
		await webView.AddScriptToExecuteOnDocumentCreatedAsync(Script);
	}
}
