import type { Metadata } from "next"; import { getProduct } from "../../docs-data"; import { ProductPage } from "../../product-page";
const product = getProduct("runic-command-line"); export const metadata: Metadata = { title: product.name, description: product.summary }; export default function Page() { return <ProductPage product={product} />; }
