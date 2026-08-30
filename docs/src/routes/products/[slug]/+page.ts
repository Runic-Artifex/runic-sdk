import { error } from '@sveltejs/kit';
import { getProduct, products } from '$lib/docs-data';
import type { PageLoad } from './$types';

export const entries = () =>
  products.map((product) => ({ slug: product.slug }));

export const load: PageLoad = ({ params }) => {
  const product = getProduct(params.slug);
  if (!product) error(404, 'Product not found');
  return { product };
};
