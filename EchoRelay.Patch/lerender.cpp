#include "lerender.h"
#include <d3d11.h>
#include <detours.h>
#include <cstdio>
#include <share.h>

namespace LeRender
{
	static VOID(*g_log)(const CHAR* format, ...) = NULL;

	typedef HRESULT(WINAPI* CreateDeviceFn)(IDXGIAdapter* adapter, D3D_DRIVER_TYPE driverType, HMODULE software, UINT flags,
		const D3D_FEATURE_LEVEL* levels, UINT levelCount, UINT sdkVersion, ID3D11Device** device, D3D_FEATURE_LEVEL* level,
		ID3D11DeviceContext** context);
	static CreateDeviceFn g_originalCreateDevice = NULL;

	// ID3D11DeviceContext vtable slots (IUnknown 0-2, ID3D11DeviceChild 3-6, then the context methods in order).
	static const int SLOT_DRAW_INDEXED = 12, SLOT_DRAW = 13, SLOT_DRAW_INDEXED_INSTANCED = 20, SLOT_DRAW_INSTANCED = 21;
	typedef VOID(STDMETHODCALLTYPE* DrawIndexedFn)(ID3D11DeviceContext*, UINT, UINT, INT);
	typedef VOID(STDMETHODCALLTYPE* DrawFn)(ID3D11DeviceContext*, UINT, UINT);
	typedef VOID(STDMETHODCALLTYPE* DrawIndexedInstancedFn)(ID3D11DeviceContext*, UINT, UINT, UINT, INT, UINT);
	typedef VOID(STDMETHODCALLTYPE* DrawInstancedFn)(ID3D11DeviceContext*, UINT, UINT, UINT, UINT);
	static DrawIndexedFn g_drawIndexed = NULL;
	static DrawFn g_draw = NULL;
	static DrawIndexedInstancedFn g_drawIndexedInstanced = NULL;
	static DrawInstancedFn g_drawInstanced = NULL;
	/// The original draw functions of each patched context vtable (immediate and deferred contexts can differ).
	struct Originals { VOID** vtable; DrawIndexedFn drawIndexed; DrawFn draw; DrawIndexedInstancedFn drawIndexedInstanced; DrawInstancedFn drawInstanced; };
	static Originals g_originals[8] = {};
	static const Originals& OriginalsOf(ID3D11DeviceContext* context)
	{
		VOID** vtable = *(VOID***)context;
		for (const Originals& o : g_originals)
			if (o.vtable == vtable)
				return o;
		return g_originals[0];
	}

	static volatile LONG g_logRemaining = 0;
	static volatile LONG g_drawCalls = 0, g_clearCalls = 0, g_executeCalls = 0;
	static FILE* g_drawLog = NULL;
	static LONG g_drawNumber = 0;

	static VOID OpenDrawLog()
	{
		if (g_drawLog != NULL)
			return;
		CHAR path[MAX_PATH];
		GetModuleFileNameA(NULL, path, MAX_PATH);
		CHAR* slash = strrchr(path, '\\');
		if (slash != NULL)
			*(slash + 1) = 0;
		strcat_s(path, "le_draws.log");
		g_drawLog = _fsopen(path, "w", _SH_DENYNO);
	}

	static UINT BufferSize(ID3D11Buffer* buffer, UINT* stride)
	{
		D3D11_BUFFER_DESC desc = {};
		buffer->GetDesc(&desc);
		if (stride != NULL)
			*stride = desc.StructureByteStride;
		return desc.ByteWidth;
	}

	/// One line per draw: the call, the shaders and what's bound to the vertex shader (constant buffers, resources).
	static VOID LogDraw(ID3D11DeviceContext* context, const CHAR* kind, UINT count, UINT instances, UINT start, INT baseVertex)
	{
		if (g_drawLog == NULL)
			return;
		ID3D11VertexShader* vs = NULL; ID3D11PixelShader* ps = NULL;
		context->VSGetShader(&vs, NULL, NULL);
		context->PSGetShader(&ps, NULL, NULL);
		fprintf(g_drawLog, "%5ld %s count %u inst %u start %u base %d vs %p ps %p", g_drawNumber, kind, count, instances, start,
			baseVertex, (void*)vs, (void*)ps);
		ID3D11Buffer* cbs[8] = {};
		context->VSGetConstantBuffers(0, 8, cbs);
		for (int i = 0; i < 8; i++)
			if (cbs[i] != NULL)
			{
				fprintf(g_drawLog, " cb%d %p(%u)", i, (void*)cbs[i], BufferSize(cbs[i], NULL));
				cbs[i]->Release();
			}
		ID3D11ShaderResourceView* srvs[8] = {};
		context->VSGetShaderResources(0, 8, srvs);
		for (int i = 0; i < 8; i++)
			if (srvs[i] != NULL)
			{
				D3D11_SHADER_RESOURCE_VIEW_DESC desc = {};
				srvs[i]->GetDesc(&desc);
				ID3D11Resource* resource = NULL;
				srvs[i]->GetResource(&resource);
				UINT stride = 0, size = 0;
				D3D11_RESOURCE_DIMENSION dimension = D3D11_RESOURCE_DIMENSION_UNKNOWN;
				if (resource != NULL)
				{
					resource->GetType(&dimension);
					if (dimension == D3D11_RESOURCE_DIMENSION_BUFFER)
						size = BufferSize((ID3D11Buffer*)resource, &stride);
					resource->Release();
				}
				fprintf(g_drawLog, " t%d %p(dim %d size %u stride %u)", i, (void*)srvs[i], (int)desc.ViewDimension, size, stride);
				srvs[i]->Release();
			}
		ID3D11Buffer* vb = NULL; UINT vbStride = 0, vbOffset = 0;
		context->IAGetVertexBuffers(0, 1, &vb, &vbStride, &vbOffset);
		if (vb != NULL)
		{
			fprintf(g_drawLog, " vb %p(%u, stride %u)", (void*)vb, BufferSize(vb, NULL), vbStride);
			vb->Release();
		}
		fputc('\n', g_drawLog);
		if (vs != NULL) vs->Release();
		if (ps != NULL) ps->Release();
	}

	static BOOL Logging()
	{
		InterlockedIncrement(&g_drawCalls);
		if (g_logRemaining <= 0)
			return FALSE;
		g_drawNumber++;
		if (InterlockedDecrement(&g_logRemaining) == 0)
		{
			fflush(g_drawLog);
			g_log("[COOP] draw log written (le_draws.log, %ld draws)", g_drawNumber);
		}
		return TRUE;
	}

	static VOID STDMETHODCALLTYPE HookedDrawIndexed(ID3D11DeviceContext* context, UINT count, UINT start, INT baseVertex)
	{
		if (Logging()) LogDraw(context, "DrawIndexed", count, 1, start, baseVertex);
		OriginalsOf(context).drawIndexed(context, count, start, baseVertex);
	}
	static VOID STDMETHODCALLTYPE HookedDraw(ID3D11DeviceContext* context, UINT count, UINT start)
	{
		if (Logging()) LogDraw(context, "Draw", count, 1, start, 0);
		OriginalsOf(context).draw(context, count, start);
	}
	static VOID STDMETHODCALLTYPE HookedDrawIndexedInstanced(ID3D11DeviceContext* context, UINT count, UINT instances, UINT start,
		INT baseVertex, UINT startInstance)
	{
		if (Logging()) LogDraw(context, "DrawIndexedInstanced", count, instances, start, baseVertex);
		OriginalsOf(context).drawIndexedInstanced(context, count, instances, start, baseVertex, startInstance);
	}
	static VOID STDMETHODCALLTYPE HookedDrawInstanced(ID3D11DeviceContext* context, UINT count, UINT instances, UINT start,
		UINT startInstance)
	{
		if (Logging()) LogDraw(context, "DrawInstanced", count, instances, start, 0);
		OriginalsOf(context).drawInstanced(context, count, instances, start, startInstance);
	}

	typedef VOID(STDMETHODCALLTYPE* ClearFn)(ID3D11DeviceContext*, ID3D11RenderTargetView*, const FLOAT*);
	typedef VOID(STDMETHODCALLTYPE* ExecuteFn)(ID3D11DeviceContext*, ID3D11CommandList*, BOOL);
	static ClearFn g_clear = NULL;
	static ExecuteFn g_execute = NULL;
	static VOID STDMETHODCALLTYPE HookedClear(ID3D11DeviceContext* c, ID3D11RenderTargetView* v, const FLOAT* color)
	{
		InterlockedIncrement(&g_clearCalls);
		g_clear(c, v, color);
	}
	static VOID STDMETHODCALLTYPE HookedExecute(ID3D11DeviceContext* c, ID3D11CommandList* list, BOOL restore)
	{
		InterlockedIncrement(&g_executeCalls);
		g_execute(c, list, restore);
	}

	static DWORD WINAPI CountThread(LPVOID)
	{
		for (int i = 0; i < 24; i++)
		{
			Sleep(5000);
			g_log("[COOP] last 5 s: %ld draws, %ld render target clears, %ld command lists executed (hooked contexts)",
				InterlockedExchange(&g_drawCalls, 0), InterlockedExchange(&g_clearCalls, 0), InterlockedExchange(&g_executeCalls, 0));
		}
		return 0;
	}

	static VOID PatchSlot(VOID** vtable, int slot, VOID* hook, VOID** original)
	{
		DWORD old;
		VirtualProtect(&vtable[slot], sizeof(VOID*), PAGE_READWRITE, &old);
		*original = vtable[slot];
		vtable[slot] = hook;
		VirtualProtect(&vtable[slot], sizeof(VOID*), old, &old);
	}

	/// Patches a context's draw slots. Deferred contexts (recorded on worker threads, replayed with ExecuteCommandList) may
	/// have their own vtable, so each one is checked; vtables already patched are left alone.
	static VOID PatchContext(ID3D11DeviceContext* context, const CHAR* what)
	{
		VOID** vtable = *(VOID***)context;
		Originals* free = NULL;
		for (Originals& o : g_originals)
		{
			if (o.vtable == vtable)
				return;
			if (o.vtable == NULL && free == NULL)
				free = &o;
		}
		if (free == NULL)
			return;
		// Originals first, then the vtable entry, then the patches: a hook that runs meanwhile finds its originals.
		free->drawIndexed = (DrawIndexedFn)vtable[SLOT_DRAW_INDEXED];
		free->draw = (DrawFn)vtable[SLOT_DRAW];
		free->drawIndexedInstanced = (DrawIndexedInstancedFn)vtable[SLOT_DRAW_INDEXED_INSTANCED];
		free->drawInstanced = (DrawInstancedFn)vtable[SLOT_DRAW_INSTANCED];
		MemoryBarrier();
		free->vtable = vtable;
		MemoryBarrier();
		VOID* original = NULL;
		PatchSlot(vtable, SLOT_DRAW_INDEXED, (VOID*)HookedDrawIndexed, &original);
		PatchSlot(vtable, SLOT_DRAW, (VOID*)HookedDraw, &original);
		PatchSlot(vtable, SLOT_DRAW_INDEXED_INSTANCED, (VOID*)HookedDrawIndexedInstanced, &original);
		PatchSlot(vtable, SLOT_DRAW_INSTANCED, (VOID*)HookedDrawInstanced, &original);
		g_log("[COOP] hooked the draw calls of a %s (vtable %p)", what, (void*)vtable);
	}

	// ID3D11Device::CreateDeferredContext is vtable slot 27.
	typedef HRESULT(STDMETHODCALLTYPE* CreateDeferredFn)(ID3D11Device*, UINT, ID3D11DeviceContext**);
	static CreateDeferredFn g_createDeferred = NULL;
	static HRESULT STDMETHODCALLTYPE HookedCreateDeferred(ID3D11Device* device, UINT flags, ID3D11DeviceContext** context)
	{
		HRESULT result = g_createDeferred(device, flags, context);
		if (SUCCEEDED(result) && context != NULL && *context != NULL)
			PatchContext(*context, "deferred context");
		return result;
	}

	// ID3D11Device1::CreateDeferredContext1 (slot 44): its context is an ID3D11DeviceContext1, same draw slots.
	typedef HRESULT(STDMETHODCALLTYPE* CreateDeferred1Fn)(ID3D11Device*, UINT, ID3D11DeviceContext**);
	static CreateDeferred1Fn g_createDeferred1 = NULL;
	static HRESULT STDMETHODCALLTYPE HookedCreateDeferred1(ID3D11Device* device, UINT flags, ID3D11DeviceContext** context)
	{
		HRESULT result = g_createDeferred1(device, flags, context);
		if (SUCCEEDED(result) && context != NULL && *context != NULL)
			PatchContext(*context, "deferred context (1)");
		return result;
	}

	static HRESULT WINAPI HookedCreateDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE driverType, HMODULE software, UINT flags,
		const D3D_FEATURE_LEVEL* levels, UINT levelCount, UINT sdkVersion, ID3D11Device** device, D3D_FEATURE_LEVEL* level,
		ID3D11DeviceContext** context)
	{
		HRESULT result = g_originalCreateDevice(adapter, driverType, software, flags, levels, levelCount, sdkVersion, device, level, context);
		g_log("[COOP] D3D11CreateDevice -> device %p context %p", device ? (void*)*device : NULL, context ? (void*)*context : NULL);
		if (SUCCEEDED(result) && context != NULL && *context != NULL)
		{
			PatchContext(*context, "immediate context");
			if (g_clear == NULL)
			{
				VOID** vt = *(VOID***)*context;
				PatchSlot(vt, 50, (VOID*)HookedClear, (VOID**)&g_clear);
				PatchSlot(vt, 58, (VOID*)HookedExecute, (VOID**)&g_execute);
				CreateThread(NULL, 0, CountThread, NULL, 0, NULL);
			}
		}
		if (SUCCEEDED(result) && device != NULL && *device != NULL && g_createDeferred == NULL)
		{
			VOID** vtable = *(VOID***)*device;
			PatchSlot(vtable, 27, (VOID*)HookedCreateDeferred, (VOID**)&g_createDeferred);
			PatchSlot(vtable, 44, (VOID*)HookedCreateDeferred1, (VOID**)&g_createDeferred1);
			g_log("[COOP] hooked CreateDeferredContext / CreateDeferredContext1");
		}
		return result;
	}

	typedef HRESULT(WINAPI* CreateDeviceAndSwapChainFn)(IDXGIAdapter*, D3D_DRIVER_TYPE, HMODULE, UINT, const D3D_FEATURE_LEVEL*, UINT,
		UINT, const DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**, ID3D11Device**, D3D_FEATURE_LEVEL*, ID3D11DeviceContext**);
	static CreateDeviceAndSwapChainFn g_originalCreateWithSwapChain = NULL;
	static HRESULT WINAPI HookedCreateWithSwapChain(IDXGIAdapter* a, D3D_DRIVER_TYPE t, HMODULE m, UINT f, const D3D_FEATURE_LEVEL* l,
		UINT n, UINT v, const DXGI_SWAP_CHAIN_DESC* d, IDXGISwapChain** sc, ID3D11Device** device, D3D_FEATURE_LEVEL* fl,
		ID3D11DeviceContext** context)
	{
		HRESULT result = g_originalCreateWithSwapChain(a, t, m, f, l, n, v, d, sc, device, fl, context);
		g_log("[COOP] D3D11CreateDeviceAndSwapChain -> device %p context %p", device ? (void*)*device : NULL, context ? (void*)*context : NULL);
		if (SUCCEEDED(result) && context != NULL && *context != NULL)
			PatchContext(*context, "immediate context (with swap chain)");
		return result;
	}

	VOID Install(VOID(*log)(const CHAR* format, ...))
	{
		g_log = log;
		HMODULE d3d11 = LoadLibraryA("d3d11.dll");
		g_originalCreateDevice = (CreateDeviceFn)GetProcAddress(d3d11, "D3D11CreateDevice");
		if (g_originalCreateDevice == NULL)
		{
			log("[COOP] D3D11CreateDevice not found");
			return;
		}
		DetourTransactionBegin();
		DetourUpdateThread(GetCurrentThread());
		DetourAttach((PVOID*)&g_originalCreateDevice, (PVOID)HookedCreateDevice);
		g_originalCreateWithSwapChain = (CreateDeviceAndSwapChainFn)GetProcAddress(d3d11, "D3D11CreateDeviceAndSwapChain");
		if (g_originalCreateWithSwapChain != NULL)
			DetourAttach((PVOID*)&g_originalCreateWithSwapChain, (PVOID)HookedCreateWithSwapChain);
		LONG error = DetourTransactionCommit();
		log("[COOP] Direct3D 11 device hook: %s", error == NO_ERROR ? "installed" : "FAILED");
	}

	VOID RequestDrawLog()
	{
		OpenDrawLog();
		if (g_drawLog == NULL)
			return;
		g_drawNumber = 0;
		fprintf(g_drawLog, "---- draw log ----\n");
		InterlockedExchange(&g_logRemaining, 4000);
		g_log("[COOP] logging the next 4000 draw calls to le_draws.log");
	}
}
