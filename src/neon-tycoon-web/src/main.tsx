import{createRoot}from'react-dom/client';import{QueryClient,QueryClientProvider}from'@tanstack/react-query';import App from './App';import './style.css';
createRoot(document.getElementById('root')!).render(<QueryClientProvider client={new QueryClient({defaultOptions:{queries:{staleTime:30000,retry:1,refetchOnWindowFocus:true}}})}><App/></QueryClientProvider>);
